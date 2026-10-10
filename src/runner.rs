use crate::{
    pipeline::Pipeline,
    protocol::protocol_hashes,
    snapshot::{Report, SeedDiagnostics, Source, UPSTREAM_COMMIT},
    validation::validate_snapshot,
};
use serde::Deserialize;
use std::{
    fs::OpenOptions,
    io::Write,
    path::Path,
    time::{Instant, SystemTime, UNIX_EPOCH},
};
use zzz_gamedata::GameData;
use zzz_wire::kcp::Direction;

pub fn unix_seconds() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64
}

pub fn candidates<'a>(
    data: &'a GameData,
    label: Option<&str>,
) -> Result<Vec<Pipeline<'a>>, String> {
    let mut result = Vec::new();
    for name in data.datamine.xor_seeds.keys() {
        if label.is_some_and(|wanted| wanted != name) {
            continue;
        }
        let seed = data
            .seed_for_region(name)
            .ok_or("invalid_pinned_seed_metadata")?;
        result.push(Pipeline::new(name.clone(), seed, data));
    }
    if result.is_empty() {
        return Err("seed_label_not_in_pinned_metadata".into());
    }
    Ok(result)
}

pub fn feed_candidates(
    pipelines: &mut [Pipeline<'_>],
    selected: &mut Option<usize>,
    bytes: &[u8],
    direction: Direction,
    timestamp: i64,
) -> Result<bool, String> {
    if let Some(index) = *selected {
        pipelines[index].feed(bytes, direction, timestamp);
        return Ok(pipelines[index].state.coverage.all_received());
    }
    for pipeline in pipelines.iter_mut() {
        pipeline.feed(bytes, direction, timestamp);
    }
    let matching: Vec<usize> = pipelines
        .iter()
        .enumerate()
        .filter(|(_, pipeline)| pipeline.handshake_complete())
        .map(|(index, _)| index)
        .collect();
    match matching.as_slice() {
        [] => Ok(false),
        [index] => {
            *selected = Some(*index);
            eprintln!("status:login_handshake_observed");
            Ok(pipelines[*index].state.coverage.all_received())
        }
        _ => Err("ambiguous_session_seed".into()),
    }
}

pub struct RunContext {
    pub started: Instant,
    pub started_unix: i64,
    pub game_region: String,
    pub account_label: String,
    pub mode: String,
}

pub fn finalize(
    mut pipelines: Vec<Pipeline<'_>>,
    selected: Option<usize>,
    context: RunContext,
    stop_reason: &str,
) -> Report {
    let seed_diagnostics = pipelines
        .iter()
        .map(|pipeline| SeedDiagnostics {
            seed_label: pipeline.seed_label.clone(),
            handshake: pipeline.state.counters.handshake.clone(),
            session_established: pipeline.handshake_complete(),
        })
        .collect();
    let index = selected.unwrap_or(0);
    let mut chosen = pipelines.swap_remove(index);
    let seed_label = selected.map(|_| chosen.seed_label.clone());
    chosen.finalize_transport();
    validate_snapshot(&mut chosen.state);
    let source = Source {
        upstream_commit: UPSTREAM_COMMIT,
        protocol_version: "3.2".into(),
        requested_game_region: context.game_region,
        decoded_seed_label: seed_label,
        account_label: context.account_label,
        game_account_identity: "not_collected_or_verified",
        capture_started_unix_seconds: context.started_unix,
        elapsed_ms: context.started.elapsed().as_millis(),
        mode: context.mode,
        protocol_hashes: protocol_hashes(),
    };
    let mut report = chosen.state.report(source, stop_reason);
    report.seed_diagnostics = seed_diagnostics;
    report
}

/// A fault after capture starts must preserve the diagnostic/asset observations.
/// Only fixed fault categories reach JSON; arbitrary error payloads are excluded.
pub fn record_run_fault(pipelines: &mut [Pipeline<'_>], error: &str) -> &'static str {
    let code = match error {
        "ambiguous_session_seed" => "ambiguous_session_seed",
        "capture_timer_failed" => "capture_timer_failed",
        _ => "capture_receive_failed",
    };
    for pipeline in pipelines {
        pipeline.state.issue(code, "snapshot", "capture");
    }
    "capture_failed"
}

pub fn write_report(path: &Path, report: &Report) -> Result<(), String> {
    let bytes = serde_json::to_vec_pretty(report).map_err(|_| "report_serialization_failed")?;
    let mut file = OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(path)
        .map_err(|_| "output_create_failed_or_already_exists")?;
    file.write_all(&bytes)
        .and_then(|_| file.sync_all())
        .map_err(|_| "output_write_failed")?;
    Ok(())
}

#[derive(Deserialize)]
#[serde(untagged)]
enum ReplayDirection {
    Number(u64),
    Name(String),
}

#[derive(Deserialize)]
pub struct ReplayPacket {
    direction: ReplayDirection,
    pub timestamp: i64,
    pub data: Vec<u8>,
}

impl ReplayPacket {
    pub fn direction(&self) -> Result<Direction, String> {
        match &self.direction {
            ReplayDirection::Number(0) => Ok(Direction::Incoming),
            ReplayDirection::Number(1) => Ok(Direction::Outgoing),
            ReplayDirection::Name(name) if name == "incoming" => Ok(Direction::Incoming),
            ReplayDirection::Name(name) if name == "outgoing" => Ok(Direction::Outgoing),
            _ => Err("invalid_replay_direction".into()),
        }
    }
}

#[derive(Deserialize)]
pub struct Replay {
    pub packets: Vec<ReplayPacket>,
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::types::{DiscRecord, LoadRecords, Presence, ScalarObservation};

    #[test]
    fn receive_failure_preserves_partial_observations_and_blocks_import() {
        let data = crate::protocol::load_protocol(
            &Path::new(env!("CARGO_MANIFEST_DIR")).join(".upstream/assets"),
        )
        .unwrap();
        let mut pipelines = candidates(&data, None).unwrap();
        let observed = |value| ScalarObservation {
            value: Some(value),
            presence: Presence::Present,
        };
        pipelines[0]
            .state
            .accept(LoadRecords::Discs(vec![DiscRecord {
                uid: observed(1),
                catalog_id: observed(31041),
                level: observed(15),
                main_stat: None,
                substats: vec![],
            }]));
        let reason = record_run_fault(&mut pipelines, "capture_receive_failed");
        let report = finalize(
            pipelines,
            Some(0),
            RunContext {
                started: Instant::now(),
                started_unix: 1,
                game_region: "synthetic".into(),
                account_label: "synthetic".into(),
                mode: "synthetic".into(),
            },
            reason,
        );
        assert_eq!(report.stop_reason, "capture_failed");
        assert_eq!(report.counts.discs, 1);
        assert!(report.coverage.disc_load_received);
        assert!(!report.coverage.all_received());
        assert!(report
            .issues
            .iter()
            .any(|issue| issue.code == "capture_receive_failed"));
        assert!(!report.ready_for_account_import);
        assert!(!report.full_inventory_verified);
    }
    #[test]
    fn replay_directions_cannot_wrap_large_numbers_to_incoming() {
        let replay: Replay =
            serde_json::from_str(r#"{"packets":[{"direction":256,"timestamp":1,"data":[] }]}"#)
                .unwrap();
        assert!(replay.packets[0].direction().is_err());
    }
}
