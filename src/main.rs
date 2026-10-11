use clap::{Parser, Subcommand};
use soda_asset_capture::{
    client_dispatch, cn_dispatch, live,
    protocol::{load_protocol, protocol_hashes},
    runner::{
        candidates, feed_candidates, finalize, unix_seconds, write_report, Replay, RunContext,
    },
};
use std::{fs, path::PathBuf, time::Instant};

#[derive(Parser)]
#[command(
    version,
    about = "Soda local login asset snapshot probe; never writes a Soda account"
)]
struct Cli {
    #[arg(long, global = true)]
    assets: Option<PathBuf>,
    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    /// Inspect pinned protocol support without opening WinDivert or observing traffic.
    Inspect,
    /// Explicit selected-agent local diagnostic; never saves a full inventory or imports.
    #[cfg(any())]
    FieldSurvey {
        #[arg(long)]
        out: PathBuf,
        #[arg(long = "agent-id", required = true, value_delimiter = ',', num_args = 1..=3)]
        agent_ids: Vec<u32>,
        #[arg(long)]
        client_resources: PathBuf,
        #[arg(long, default_value_t = 120, value_parser = clap::value_parser!(u64).range(1..=180))]
        seconds: u64,
    },
    /// Inspect two approved static client fields; no requests, game, or driver.
    InspectClientDispatch {
        #[arg(long)]
        input: PathBuf,
        #[arg(long)]
        out: PathBuf,
    },
    /// Run one approved anonymous CN configuration check; never start game capture.
    CheckCnConfig {
        #[arg(long)]
        out: PathBuf,
        /// Separately approved resources.assets input; validate before any GET.
        #[arg(long)]
        client_resources: Option<PathBuf>,
    },
    /// Capture copies of UDP/20501 for one normal login, then save an asset-only candidate.
    Capture {
        #[arg(long)]
        out: PathBuf,
        #[arg(long, default_value_t = 120, value_parser = clap::value_parser!(u64).range(1..=180))]
        seconds: u64,
        #[arg(long, default_value = "CN")]
        game_region: String,
        #[arg(long, default_value = "国服验证")]
        account_label: String,
        #[arg(long)]
        seed_label: Option<String>,
        /// Explicitly approved two-GET public CN configuration bootstrap; default off.
        #[arg(long, conflicts_with = "seed_label")]
        cn_public_bootstrap: bool,
        /// Approved local resource fields and two-GET bootstrap before capture.
        #[arg(long, conflicts_with_all = ["seed_label", "cn_public_bootstrap"])]
        client_resources: Option<PathBuf>,
    },
    /// Read an explicitly supplied offline recording; never save raw traffic or open a driver.
    Replay {
        #[arg(long)]
        input: PathBuf,
        #[arg(long)]
        out: PathBuf,
        #[arg(long, default_value = "CN")]
        game_region: String,
        #[arg(long, default_value = "离线验证")]
        account_label: String,
        #[arg(long)]
        seed_label: Option<String>,
    },
}

fn main() -> std::process::ExitCode {
    match run(Cli::parse()) {
        Ok(code) => std::process::ExitCode::from(code),
        Err(code) => {
            eprintln!("error:{code}");
            std::process::ExitCode::from(1)
        }
    }
}

fn run(cli: Cli) -> Result<u8, String> {
    #[cfg(any())]
    if let Command::FieldSurvey { out, agent_ids, .. } = &cli.command {
        soda_asset_capture::field_survey::validate_agent_ids(agent_ids)?;
        soda_asset_capture::field_survey_run::validate_output(out)?;
    }
    if let Command::InspectClientDispatch { input, out } = &cli.command {
        ensure_new_output(out)?;
        let mut output = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(out)
            .map_err(|_| "client_field_receipt_create_new_failed")?;
        let receipt = client_dispatch::inspect(input).into_receipt();
        let exit_code = u8::from(receipt.status != "client_request_fields_valid");
        let summary = serde_json::json!({
            "schema":"soda-client-dispatch-field-check/v1",
            "client_resource_fields":receipt,
            "expectedVersion":"CNPRODWin3.2.0",
            "requestsAttempted":0, "liveCaptureStarted":false,
            "rawFileSaved":false, "requestSeedSaved":false,
            "unselectedFieldsExported":false, "accountWriteEnabled":false
        });
        serde_json::to_writer_pretty(&mut output, &summary)
            .map_err(|_| "client_field_receipt_write_failed")?;
        output
            .sync_all()
            .map_err(|_| "client_field_receipt_flush_failed")?;
        println!("{summary}");
        return Ok(exit_code);
    }
    let directory = std::env::current_exe()
        .map_err(|_| "executable_path_unknown")?
        .parent()
        .ok_or("executable_directory_unknown")?
        .to_path_buf();
    let assets = cli.assets.unwrap_or_else(|| directory.join("assets"));
    let data = load_protocol(&assets)?;
    let report = match cli.command {
        #[cfg(any())]
        Command::FieldSurvey {
            out,
            agent_ids,
            client_resources,
            seconds,
        } => {
            let report = soda_asset_capture::field_survey_run::capture_selected(
                &data,
                &directory,
                &client_resources,
                &agent_ids,
                seconds,
            )?;
            write_report(&out, &report)?;
            report
        }
        Command::Inspect => {
            println!(
                "{}",
                serde_json::json!({
                    "schema": "soda-asset-capture-inspect/v1",
                    "protocolVersion": "3.2", "upstreamCommit": soda_asset_capture::snapshot::UPSTREAM_COMMIT,
                    "knownSeedLabels": data.datamine.xor_seeds.keys().collect::<Vec<_>>(),
                    "cnSupport": "unverified", "protocolHashes": protocol_hashes(),
                    "liveCaptureStarted": false, "accountWriteEnabled": false,
                    "rawTrafficSaved": false, "networkUpdatesEnabled": false,
                    "publicCnBootstrapDefaultEnabled": false
                })
            );
            return Ok(0);
        }
        Command::InspectClientDispatch { .. } => unreachable!("handled before protocol loading"),
        Command::CheckCnConfig {
            out,
            client_resources,
        } => {
            ensure_new_output(&out)?;
            // Reserve the exact new output before making either public request.
            let mut output = fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&out)
                .map_err(|_| "configuration_receipt_create_new_failed")?;
            let configuration = match client_resources {
                Some(input) => cn_dispatch::fetch_client_configuration(&input),
                None => cn_dispatch::fetch_approved_configuration(),
            };
            let (status, receipt, exit_code) = match configuration {
                Ok(bootstrap) => ("public_configuration_derived", bootstrap.into_receipt(), 0),
                Err(failure) => (failure.code, *failure.receipt, 1),
            };
            let summary = serde_json::json!({
                "schema":"soda-cn-public-configuration-check/v1",
                "phase":"public_metadata", "status":status,
                "public_metadata":receipt, "liveCaptureStarted":false,
                "rawTrafficSaved":false, "accountWriteEnabled":false,
                "initialConfigurationSaved":false
            });
            serde_json::to_writer_pretty(&mut output, &summary)
                .map_err(|_| "configuration_receipt_write_failed")?;
            output
                .sync_all()
                .map_err(|_| "configuration_receipt_flush_failed")?;
            println!("{summary}");
            return Ok(exit_code);
        }
        Command::Capture {
            out,
            seconds,
            game_region,
            account_label,
            seed_label,
            cn_public_bootstrap,
            client_resources,
        } => {
            ensure_new_output(&out)?;
            let mut pipelines = candidates(&data, seed_label.as_deref())?;
            let metadata = if cn_public_bootstrap || client_resources.is_some() {
                if game_region != "CN" {
                    return Err("public_cn_bootstrap_requires_cn_region".into());
                }
                soda_asset_capture::protocol::verify_driver_files(&directory)?;
                let configuration = match client_resources {
                    Some(input) => cn_dispatch::fetch_client_configuration(&input),
                    None => cn_dispatch::fetch_approved_configuration(),
                };
                match configuration {
                    Ok(bootstrap) => {
                        let (pipeline, receipt) = bootstrap.into_pipeline(&data);
                        pipelines.push(pipeline);
                        eprintln!("status:cn_public_metadata_derived");
                        Some(receipt)
                    }
                    Err(failure) => {
                        println!(
                            "{}",
                            serde_json::json!({
                                "phase":"public_metadata", "status":failure.code,
                                "public_metadata":failure.receipt, "liveCaptureStarted":false,
                                "rawTrafficSaved":false, "accountWriteEnabled":false
                            })
                        );
                        return Err(failure.code.into());
                    }
                }
            } else {
                None
            };
            let context = RunContext {
                started: Instant::now(),
                started_unix: unix_seconds(),
                game_region,
                account_label,
                mode: if metadata
                    .as_ref()
                    .is_some_and(|receipt| receipt.client_resource_fields.is_some())
                {
                    "live_copy_only_client_cn_configuration_candidate"
                } else if metadata.is_some() {
                    "live_copy_only_public_cn_configuration_candidate"
                } else {
                    "live_copy_only"
                }
                .into(),
            };
            let mut report = live::capture(pipelines, context, seconds)?;
            report.public_metadata = metadata;
            write_report(&out, &report)?;
            report
        }
        Command::Replay {
            input,
            out,
            game_region,
            account_label,
            seed_label,
        } => {
            ensure_new_output(&out)?;
            let bytes = fs::read(input).map_err(|_| "replay_input_unavailable")?;
            let recording: Replay =
                serde_json::from_slice(&bytes).map_err(|_| "replay_input_invalid")?;
            let mut pipelines = candidates(&data, seed_label.as_deref())?;
            let context = RunContext {
                started: Instant::now(),
                started_unix: unix_seconds(),
                game_region,
                account_label,
                mode: "offline_recording".into(),
            };
            let mut selected = None;
            let mut reason = "replay_incomplete";
            for packet in recording.packets {
                if feed_candidates(
                    &mut pipelines,
                    &mut selected,
                    &packet.data,
                    packet.direction()?,
                    packet.timestamp,
                )? {
                    reason = "load_responses_observed";
                    break;
                }
            }
            let report = finalize(pipelines, selected, context, reason);
            write_report(&out, &report)?;
            report
        }
    };
    // Stdout is a diagnostic summary, never the player's asset payload.
    println!(
        "{}",
        serde_json::json!({ "schema": report.schema, "status": report.status,
        "coverage": report.coverage, "counts": report.counts,
        "elapsedMs": report.source.elapsed_ms, "issues": report.issues,
        "cnSupport": "requires_game_count_and_field_review", "readyForAccountImport": false })
    );
    Ok(
        if report.status == "staged_for_field_review"
            || report.status == "field_survey_selected_records_observed"
        {
            0
        } else {
            2
        },
    )
}

fn ensure_new_output(path: &std::path::Path) -> Result<(), String> {
    if path.exists() {
        return Err("output_already_exists_capture_not_started".into());
    }
    if !path.parent().is_some_and(|parent| parent.is_dir()) {
        return Err("output_parent_missing_capture_not_started".into());
    }
    Ok(())
}
