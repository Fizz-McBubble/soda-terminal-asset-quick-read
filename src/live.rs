use crate::{
    pipeline::Pipeline,
    protocol::verify_driver_files,
    runner::{feed_candidates, finalize, record_run_fault, RunContext},
    snapshot::{CaptureCounters, Report},
};
use std::{
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    },
    thread,
    time::Duration,
};

#[cfg(windows)]
pub fn capture(
    mut pipelines: Vec<Pipeline<'_>>,
    context: RunContext,
    seconds: u64,
) -> Result<Report, String> {
    use zzz_capture::{Capture, CaptureError, GAME_PORT};
    let executable = std::env::current_exe().map_err(|_| "executable_path_unknown")?;
    verify_driver_files(executable.parent().ok_or("executable_directory_unknown")?)?;
    restrict_dll_search()?;
    let capture = open_capture(Capture::open, GAME_PORT)?;
    let finished = Arc::new(AtomicBool::new(false));
    let cancelled = Arc::new(AtomicBool::new(false));
    let timed_out = Arc::new(AtomicBool::new(false));
    let cancel_flag = Arc::clone(&cancelled);
    let cancel_capture = capture.clone();
    ctrlc::set_handler(move || {
        cancel_flag.store(true, Ordering::Release);
        cancel_capture.stop();
    })
    .map_err(|_| "cancel_handler_unavailable")?;
    let timer_finished = Arc::clone(&finished);
    let timeout_flag = Arc::clone(&timed_out);
    let timer_capture = capture.clone();
    let timer = thread::spawn(move || {
        let until = std::time::Instant::now() + Duration::from_secs(seconds);
        while !timer_finished.load(Ordering::Acquire) {
            if std::time::Instant::now() >= until {
                timeout_flag.store(true, Ordering::Release);
                timer_capture.stop();
                break;
            }
            thread::sleep(Duration::from_millis(25));
        }
    });
    eprintln!("status:waiting_for_normal_game_login");
    let mut selected = None;
    #[cfg(feature = "local-field-survey")]
    let survey_mode = pipelines
        .iter()
        .any(|pipeline| pipeline.state.field_survey.is_some());
    #[cfg(feature = "local-field-survey")]
    let mut survey_ready_reported = false;
    let mut buffer = vec![0u8; 65535];
    let mut reason = "capture_ended_before_all_loads";
    let mut receive_error_code = None;
    let result = (|| {
        while !cancelled.load(Ordering::Acquire) && !timed_out.load(Ordering::Acquire) {
            let received = match capture.recv(&mut buffer) {
                Ok(packet) => packet,
                Err(error)
                    if expected_shutdown(
                        &error,
                        cancelled.load(Ordering::Acquire) || timed_out.load(Ordering::Acquire),
                    ) =>
                {
                    None
                }
                Err(CaptureError::RecvFailed(code)) => {
                    receive_error_code = Some(code);
                    eprintln!("error:capture_receive_failed_win32:{code}");
                    return Err("capture_receive_failed".into());
                }
                Err(_) => return Err("capture_receive_failed".into()),
            };
            let Some(packet) = received else {
                if cancelled.load(Ordering::Acquire) || timed_out.load(Ordering::Acquire) {
                    break;
                }
                // The pinned backend maps an unexpected no-data end to Ok(None).
                // Preserve it as a receive failure instead of a partial candidate.
                return Err("capture_receive_failed".into());
            };
            // Shutdown may leave queued copies; do not drain them after cancellation.
            if cancelled.load(Ordering::Acquire) || timed_out.load(Ordering::Acquire) {
                break;
            }
            if feed_candidates(
                &mut pipelines,
                &mut selected,
                &packet.data,
                packet.direction,
                packet.timestamp,
            )? {
                reason = "load_responses_observed";
                break;
            }
            #[cfg(feature = "local-field-survey")]
            if survey_mode
                && !survey_ready_reported
                && selected.is_some_and(|index| {
                    pipelines[index]
                        .state
                        .field_survey
                        .as_ref()
                        .is_some_and(|survey| survey.all_selected_observed())
                })
            {
                eprintln!("status:selected_agent_records_observed_survey_continues");
                survey_ready_reported = true;
            }
        }
        Ok::<(), String>(())
    })();
    finished.store(true, Ordering::Release);
    capture.stop();
    if timer.join().is_err() {
        reason = record_run_fault(&mut pipelines, "capture_timer_failed");
    }
    if let Err(error) = result {
        reason = record_run_fault(&mut pipelines, &error);
    }
    let statistics = CaptureCounters {
        filtered_ip_packets: capture.packets() + capture.skipped(),
        usable_udp_packets: capture.packets(),
        rejected_ip_packets: capture.skipped(),
        direction_conflicts: capture.direction_conflicts(),
        receive_error_win32: receive_error_code,
    };
    for pipeline in &mut pipelines {
        pipeline.state.counters.capture = Some(statistics.clone());
        if statistics.rejected_ip_packets > 0 {
            pipeline
                .state
                .issue("capture_ip_packets_rejected", "snapshot", "capture");
        }
        if statistics.direction_conflicts > 0 {
            pipeline
                .state
                .issue("capture_direction_conflicts", "snapshot", "capture");
        }
    }
    if cancelled.load(Ordering::Acquire) {
        reason = "cancelled";
    } else if timed_out.load(Ordering::Acquire) {
        reason = "timed_out";
        #[cfg(feature = "local-field-survey")]
        if survey_mode {
            reason = "survey_window_elapsed";
        }
    }
    Ok(finalize(pipelines, selected, context, reason))
}

#[cfg(windows)]
fn open_capture<F>(mut open: F, port: u16) -> Result<zzz_capture::Capture, String>
where
    F: FnMut(u16) -> Result<zzz_capture::Capture, zzz_capture::CaptureError>,
{
    const OPEN_ATTEMPTS: u8 = 2;
    for attempt in 1..=OPEN_ATTEMPTS {
        match open(port) {
            Ok(capture) => return Ok(capture),
            Err(error) if retry_open_after_resource_failure(&error, attempt, OPEN_ATTEMPTS) => {
                thread::sleep(Duration::from_millis(150));
            }
            Err(error) => return Err(open_error_code(&error)),
        }
    }
    Err("capture_open_failed".into())
}

#[cfg(windows)]
fn retry_open_after_resource_failure(
    error: &zzz_capture::CaptureError,
    attempt: u8,
    attempts: u8,
) -> bool {
    matches!(
        error,
        zzz_capture::CaptureError::OpenFailed { code: 1450, .. }
    ) && attempt < attempts
}

#[cfg(windows)]
fn open_error_code(error: &zzz_capture::CaptureError) -> String {
    match error {
        zzz_capture::CaptureError::OpenFailed { code, .. } => {
            format!("capture_open_failed_win32:{code}")
        }
        zzz_capture::CaptureError::DllMissing => "capture_dll_unavailable".into(),
        _ => "capture_open_failed".into(),
    }
}

fn expected_shutdown(error: &zzz_capture::CaptureError, stop_requested: bool) -> bool {
    // WinDivert 2.2 returns ERROR_NO_DATA (232), rather than NO_MORE_ITEMS (259),
    // after shutdown empties its queue. Keep the pinned upstream files intact.
    stop_requested && matches!(error, zzz_capture::CaptureError::RecvFailed(232))
}

#[cfg(windows)]
fn restrict_dll_search() -> Result<(), String> {
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn SetDefaultDllDirectories(flags: u32) -> i32;
    }
    // Application directory plus Windows system directory; excludes working directory/PATH.
    if unsafe { SetDefaultDllDirectories(0x00000200 | 0x00000800) } == 0 {
        return Err("dll_search_restriction_failed".into());
    }
    Ok(())
}

#[cfg(not(windows))]
pub fn capture(_: Vec<Pipeline<'_>>, _: RunContext, _: u64) -> Result<Report, String> {
    Err("live_capture_requires_windows".into())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn documented_shutdown_is_normal_only_after_our_own_stop_request() {
        use zzz_capture::CaptureError;
        assert!(expected_shutdown(&CaptureError::RecvFailed(232), true));
        assert!(!expected_shutdown(&CaptureError::RecvFailed(232), false));
        assert!(!expected_shutdown(&CaptureError::RecvFailed(5), true));
        assert!(!expected_shutdown(&CaptureError::BufferTooSmall, true));
    }

    #[test]
    fn only_one_bounded_retry_follows_1450_open_failure() {
        use zzz_capture::CaptureError;
        let resource = CaptureError::OpenFailed {
            filter: "synthetic".into(),
            code: 1450,
        };
        assert!(retry_open_after_resource_failure(&resource, 1, 2));
        assert!(!retry_open_after_resource_failure(&resource, 2, 2));
        assert!(!retry_open_after_resource_failure(
            &CaptureError::OpenFailed {
                filter: "synthetic".into(),
                code: 577,
            },
            1,
            2
        ));
    }
}
