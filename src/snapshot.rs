use crate::{
    response::ResponseObservation,
    types::{AgentRecord, DecodeIssue, DiscRecord, EngineRecord, LoadRecords},
};
use serde::Serialize;

pub const SCHEMA: &str = "soda-asset-snapshot-probe/v1";
pub const UPSTREAM_COMMIT: &str = "4853f07c8fb5322c12b9b580d6364b577c2ac96a";

#[derive(Debug, Default, Clone, Serialize)]
pub struct Assets {
    pub discs: Vec<DiscRecord>,
    pub engines: Vec<EngineRecord>,
    pub agents: Vec<AgentRecord>,
}

#[derive(Debug, Default, Clone, Serialize)]
pub struct Coverage {
    pub disc_load_received: bool,
    pub engine_load_received: bool,
    pub agent_load_received: bool,
}

impl Coverage {
    pub fn all_received(&self) -> bool {
        self.disc_load_received && self.engine_load_received && self.agent_load_received
    }
}

#[derive(Debug, Default, Clone, Serialize)]
pub struct CaptureCounters {
    pub filtered_ip_packets: u64,
    pub usable_udp_packets: u64,
    pub rejected_ip_packets: u64,
    pub direction_conflicts: u64,
    pub receive_error_win32: Option<u32>,
}

#[derive(Debug, Default, Clone, Serialize)]
pub struct FrameCounters {
    pub reassembled_messages: u64,
    pub complete_frames: u64,
    pub short_headers: u64,
    pub declared_body_shortfalls: u64,
    pub frames_with_trailing_bytes: u64,
    pub trailing_bytes_total: u64,
    pub invalid_declared_lengths: u64,
}

#[derive(Debug, Default, Clone, Serialize)]
pub struct TokenBodyCounters {
    pub protobuf_parse_failures: u64,
    pub protobuf_parse_successes: u64,
    pub length_delimited_fields: u64,
    pub non_utf8_fields: u64,
    pub base64_non_rsa_fields: u64,
    pub rsa_block_candidates: u64,
    pub rsa_decode_failures: u64,
    pub rsa_plain_length_mismatches: u64,
    pub eight_byte_candidates: u64,
    pub frames_without_usable_candidate: u64,
}

#[derive(Debug, Default, Clone, Serialize)]
pub struct HandshakeCounters {
    pub token_response_frames: u64,
    pub incoming_token_response_frames: u64,
    pub outgoing_token_response_frames: u64,
    // Missing in old reports means unobserved, not a zero count.
    pub token_body: TokenBodyCounters,
    pub server_key_extractions: u64,
    pub server_key_decode_failures: u64,
    pub session_derivation_attempts: u64,
    pub session_derivation_failures: u64,
}

#[derive(Debug, Clone, Serialize)]
pub struct SeedDiagnostics {
    pub seed_label: String,
    pub handshake: HandshakeCounters,
    pub session_established: bool,
}

#[derive(Debug, Default, Clone, Serialize)]
pub struct Counters {
    // None in replay and older reports: do not infer zero driver packets from it.
    pub capture: Option<CaptureCounters>,
    pub udp_packets: u64,
    pub frames: FrameCounters,
    pub handshake: HandshakeCounters,
    pub decoded_messages: u64,
    pub decode_failures_after_handshake: u64,
    pub truncated_messages: u64,
    pub connection_resets: u64,
    pub skipped_gaps: u64,
    pub backlog_overflows: u64,
    pub incremental_messages_ignored: u64,
}

#[derive(Debug, Serialize)]
pub struct Counts {
    pub discs: usize,
    pub s_discs: usize,
    pub a_discs: usize,
    pub b_discs: usize,
    pub unknown_rarity_discs: usize,
    pub engines: usize,
    pub agents: usize,
}

#[derive(Debug, Serialize)]
pub struct Source {
    pub upstream_commit: &'static str,
    pub protocol_version: String,
    pub requested_game_region: String,
    pub decoded_seed_label: Option<String>,
    pub account_label: String,
    pub game_account_identity: &'static str,
    pub capture_started_unix_seconds: i64,
    pub elapsed_ms: u128,
    pub mode: String,
    pub protocol_hashes: std::collections::BTreeMap<String, String>,
}

#[derive(Debug, Serialize)]
pub struct Report {
    pub schema: &'static str,
    pub status: String,
    pub stop_reason: String,
    pub source: Source,
    pub coverage: Coverage,
    pub counts: Counts,
    pub counters: Counters,
    pub response_observations: Vec<ResponseObservation>,
    pub seed_diagnostics: Vec<SeedDiagnostics>,
    pub public_metadata: Option<crate::cn_dispatch::MetadataReceipt>,
    #[serde(skip_serializing_if = "Option::is_none")]
    #[cfg(any())]
    pub field_survey: Option<crate::field_survey::FieldSurvey>,
    pub issues: Vec<DecodeIssue>,
    pub assets: Assets,
    // Receiving a load response is not independent proof of its total or scope.
    pub independent_inventory_total_verified: bool,
    pub full_inventory_verified: bool,
    pub ready_for_account_import: bool,
    pub account_write_enabled: bool,
    pub raw_traffic_saved: bool,
    pub response_status_semantics_verified: bool,
}

#[derive(Debug, Default)]
pub struct SnapshotState {
    pub coverage: Coverage,
    pub assets: Assets,
    pub counters: Counters,
    pub issues: Vec<DecodeIssue>,
    pub response_observations: Vec<ResponseObservation>,
    #[cfg(any())]
    pub field_survey: Option<crate::field_survey::FieldSurvey>,
}

impl SnapshotState {
    pub fn accept(&mut self, records: LoadRecords) {
        let already_received = match records {
            LoadRecords::Discs(ref values) => {
                let previous = self.coverage.disc_load_received;
                if !previous {
                    self.assets.discs.clone_from(values);
                    self.coverage.disc_load_received = true;
                }
                previous
            }
            LoadRecords::Engines(ref values) => {
                let previous = self.coverage.engine_load_received;
                if !previous {
                    self.assets.engines.clone_from(values);
                    self.coverage.engine_load_received = true;
                }
                previous
            }
            LoadRecords::Agents(ref values) => {
                let previous = self.coverage.agent_load_received;
                if !previous {
                    self.assets.agents.clone_from(values);
                    self.coverage.agent_load_received = true;
                }
                previous
            }
        };
        if already_received {
            self.issue(
                "repeated_load_requires_scope_review",
                "snapshot",
                "coverage",
            );
        }
    }

    pub fn issue(&mut self, code: &str, asset: &str, field: &str) {
        if self.issues.len() < 100 {
            self.issues.push(DecodeIssue {
                code: code.into(),
                asset: asset.into(),
                field: field.into(),
            });
        }
    }

    pub fn counts(&self) -> Counts {
        let mut counts = Counts {
            discs: self.assets.discs.len(),
            engines: self.assets.engines.len(),
            agents: self.assets.agents.len(),
            s_discs: 0,
            a_discs: 0,
            b_discs: 0,
            unknown_rarity_discs: 0,
        };
        for disc in &self.assets.discs {
            match disc.catalog_id.value.map(|id| id / 10 % 10 + 1) {
                Some(5) => counts.s_discs += 1,
                Some(4) => counts.a_discs += 1,
                Some(3) => counts.b_discs += 1,
                _ => counts.unknown_rarity_discs += 1,
            }
        }
        counts
    }

    pub fn report(self, source: Source, stop_reason: &str) -> Report {
        let status = if stop_reason == "cancelled" || stop_reason == "timed_out" {
            stop_reason
        } else if !self.issues.is_empty() {
            "validation_failed"
        } else if !self.response_observations.is_empty() {
            "response_semantics_unverified"
        } else if self.coverage.all_received() {
            "staged_for_field_review"
        } else {
            stop_reason
        };
        Report {
            schema: SCHEMA,
            status: status.into(),
            stop_reason: stop_reason.into(),
            source,
            counts: self.counts(),
            coverage: self.coverage,
            counters: self.counters,
            response_observations: self.response_observations,
            seed_diagnostics: Vec::new(),
            public_metadata: None,
            #[cfg(any())]
            field_survey: self.field_survey,
            issues: self.issues,
            assets: self.assets,
            independent_inventory_total_verified: false,
            full_inventory_verified: false,
            ready_for_account_import: false,
            account_write_enabled: false,
            raw_traffic_saved: false,
            response_status_semantics_verified: false,
        }
    }
}

impl Report {
    #[cfg(any())]
    pub fn redact_for_field_survey(&mut self) {
        self.schema = "soda-selected-agent-field-validation/v1";
        self.status = if self.stop_reason == "capture_failed" {
            "field_survey_capture_failed"
        } else if !self
            .field_survey
            .as_ref()
            .is_some_and(|survey| survey.session_anchor_verified)
        {
            "field_survey_session_unverified"
        } else if self
            .field_survey
            .as_ref()
            .is_some_and(|survey| survey.all_selected_observed())
        {
            if !self.issues.is_empty() {
                "field_survey_selected_records_observed_with_issues"
            } else if self
                .field_survey
                .as_ref()
                .is_some_and(|survey| survey.limits_reached)
            {
                "field_survey_selected_records_observed_with_limits"
            } else {
                "field_survey_selected_records_observed"
            }
        } else {
            "field_survey_selected_records_missing"
        }
        .into();
        self.counts = Counts {
            discs: 0,
            s_discs: 0,
            a_discs: 0,
            b_discs: 0,
            unknown_rarity_discs: 0,
            engines: 0,
            agents: self.field_survey.as_ref().map_or(0, |survey| {
                survey
                    .selected_agents
                    .iter()
                    .filter(|agent| agent.current_facts.is_some())
                    .count()
            }),
        };
        self.assets = Assets::default();
        self.account_write_enabled = false;
        self.ready_for_account_import = false;
        self.full_inventory_verified = false;
        self.independent_inventory_total_verified = false;
        self.response_status_semantics_verified = false;
    }
}
