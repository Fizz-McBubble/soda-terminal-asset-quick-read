use crate::{
    handshake::observe_token_body, observations::parse_load_response_with_potential,
    response::observe_response, snapshot::SnapshotState, types::AssetKind,
};
use zzz_crypto::xorpad::XorPad;
use zzz_gamedata::GameData;
use zzz_scan::{DecodeEvent, Decoder, Session};
use zzz_wire::{
    kcp::{Direction, Kcp, MessageHeader, MESSAGE_HEADER_SIZE},
    proto::Message,
};

pub struct Pipeline<'a> {
    pub seed_label: String,
    pub state: SnapshotState,
    kcp: Kcp,
    decoder: Decoder<'a>,
    data: &'a GameData,
    potential_field: Option<u32>,
}

impl<'a> Pipeline<'a> {
    pub fn new(seed_label: String, seed: u64, data: &'a GameData) -> Self {
        Self {
            seed_label,
            state: SnapshotState::default(),
            kcp: Kcp::new(),
            decoder: Decoder::new(
                Session::new(XorPad::for_region(seed)),
                &data.datamine,
                &data.nap,
            ),
            data,
            potential_field: crate::potential::verified_potential_field(data),
        }
    }

    pub fn handshake_complete(&self) -> bool {
        self.decoder.session().pad_ready()
    }

    #[cfg(any())]
    pub fn enable_field_survey(&mut self, ids: &[u32]) -> Result<(), String> {
        self.state.field_survey = Some(crate::field_survey::FieldSurvey::new(self.data, ids)?);
        Ok(())
    }

    pub fn feed(&mut self, bytes: &[u8], direction: Direction, unix_seconds: i64) {
        self.state.counters.udp_packets += 1;
        let messages = self.kcp.receive(bytes, direction, unix_seconds);
        let stats = self.kcp.stats();
        self.state.counters.connection_resets = stats.conv_resets;
        self.state.counters.skipped_gaps = stats.gaps_skipped;
        self.state.counters.backlog_overflows = stats.backlog_overflows;
        for message in messages {
            self.state.counters.frames.reassembled_messages += 1;
            let (header, declared_end) = match complete_message_frame(&message) {
                Ok(frame) => frame,
                Err(fault) => {
                    self.state.counters.truncated_messages += 1;
                    let frames = &mut self.state.counters.frames;
                    match fault {
                        FrameFault::ShortHeader => frames.short_headers += 1,
                        FrameFault::BodyShortfall => frames.declared_body_shortfalls += 1,
                        FrameFault::LengthOverflow => frames.invalid_declared_lengths += 1,
                    }
                    continue;
                }
            };
            self.state.counters.frames.complete_frames += 1;
            if message.len() > declared_end {
                self.state.counters.frames.frames_with_trailing_bytes += 1;
                self.state.counters.frames.trailing_bytes_total +=
                    (message.len() - declared_end) as u64;
            }
            if u32::from(header.command_id) == self.data.datamine.cmd_player_get_token_sc_rsp {
                let handshake = &mut self.state.counters.handshake;
                handshake.token_response_frames += 1;
                match direction {
                    Direction::Incoming => handshake.incoming_token_response_frames += 1,
                    Direction::Outgoing => handshake.outgoing_token_response_frames += 1,
                }
                // Observe precisely the body and stage used by the pinned extractor.
                // Keep plaintext local and never propagate it into the report.
                if self.decoder.session().server_rand_key.is_none() {
                    let body_start = MESSAGE_HEADER_SIZE + usize::from(header.head_length);
                    let plaintext = self
                        .decoder
                        .session()
                        .decrypt_body(&message[body_start..declared_end]);
                    observe_token_body(&plaintext, &mut handshake.token_body);
                }
            }
            // The pinned upstream decodes only the declared body and ignores any
            // trailing bytes. Preserve that contract, without guessing a footer's
            // meaning or permitting a short declared body.
            let message = &message[..declared_end];
            let was_ready = self.decoder.session().pad_ready();
            // Session establishment follows the production decoder for both directions.
            // Retention below remains limited to selected incoming agent records.
            let mut events = self.decoder.feed(message, unix_seconds);
            for event in &events {
                let handshake = &mut self.state.counters.handshake;
                match event {
                    DecodeEvent::ServerRandKey { .. } => handshake.server_key_extractions += 1,
                    DecodeEvent::ServerRandKeyFailed { .. } => {
                        handshake.server_key_decode_failures += 1;
                    }
                    DecodeEvent::SessionKey { .. } => handshake.session_derivation_attempts += 1,
                    DecodeEvent::SessionKeyNotFound => {
                        handshake.session_derivation_attempts += 1;
                        handshake.session_derivation_failures += 1;
                    }
                    _ => {}
                }
            }
            // The pinned decoder computes plaintext before installing the new session pad.
            // Decode that exact first frame again with the installed pad, without feeding KCP
            // twice or exporting the handshake. This keeps its first real response intact.
            if !was_ready && self.decoder.session().pad_ready() {
                events = self.decoder.feed(message, unix_seconds);
            }
            // The upstream decoder skips empty protobuf bodies. A zero-item load
            // with omitted default fields is still a response, once this session
            // is established. Its status meaning remains unverified below.
            if self.handshake_complete() && events.is_empty() {
                let command = u32::from(header.command_id);
                if header.body_length == 0
                    && [
                        self.data.datamine.cmd_get_equip_data_sc_rsp,
                        self.data.datamine.cmd_get_weapon_data_sc_rsp,
                        self.data.datamine.cmd_get_avatar_data_sc_rsp,
                    ]
                    .contains(&command)
                {
                    events.push(DecodeEvent::Message {
                        command_id: header.command_id,
                        message: Message::default(),
                        bytes: 0,
                    });
                }
            }
            for event in events {
                match event {
                    DecodeEvent::Message {
                        command_id,
                        message,
                        ..
                    } => {
                        self.state.counters.decoded_messages += 1;
                        #[cfg(any())]
                        if self.state.field_survey.is_some() && direction != Direction::Incoming {
                            continue;
                        }
                        {
                            self.accept_message(command_id, &message);
                        }
                    }
                    DecodeEvent::ProtoFailed { .. } if self.handshake_complete() => {
                        self.state.counters.decode_failures_after_handshake += 1;
                    }
                    // Handshake materials and full decoded messages are never logged or exported.
                    _ => {}
                }
            }
        }
    }

    pub fn accept_message(&mut self, command_id: u16, message: &Message) {
        let mine = &self.data.datamine;
        #[cfg(any())]
        if self.state.field_survey.is_some()
            && u32::from(command_id) != mine.cmd_get_avatar_data_sc_rsp
        {
            return;
        }
        let kind = if u32::from(command_id) == mine.cmd_get_equip_data_sc_rsp {
            AssetKind::Discs
        } else if u32::from(command_id) == mine.cmd_get_weapon_data_sc_rsp {
            AssetKind::Engines
        } else if u32::from(command_id) == mine.cmd_get_avatar_data_sc_rsp {
            AssetKind::Agents
        } else {
            if u32::from(command_id) == mine.cmd_player_sync_sc_notify
                || u32::from(command_id) == mine.cmd_dismantle_equip_cs_req
            {
                self.state.counters.incremental_messages_ignored += 1;
            }
            return;
        };
        let observation = observe_response(kind, command_id, message, &self.data.nap);
        let allowed = observation.allows_asset_observation();
        if !allowed {
            self.state
                .issue(observation.assessment, "snapshot", "response_status");
        }
        if !self
            .state
            .response_observations
            .iter()
            .any(|response| response.kind == kind)
        {
            self.state.response_observations.push(observation);
        }
        if !allowed {
            return;
        }
        #[cfg(any())]
        if let Some(survey) = &mut self.state.field_survey {
            if !survey.valid_anchor(command_id, message, self.data) {
                return;
            }
            survey.session_anchor_verified = true;
            survey.observe(message, self.data);
            // Diagnostic mode never materializes a full inventory, even in memory.
            return;
        }
        // We deliberately never call upstream Inventory::apply or its deletion fallback.
        match parse_load_response_with_potential(kind, message, mine, self.potential_field) {
            Ok(records) => self.state.accept(records),
            Err(issues) => {
                for issue in issues {
                    self.state.issue(&issue.code, &issue.asset, &issue.field);
                }
            }
        }
    }

    pub fn finalize_transport(&mut self) {
        if !self.handshake_complete() {
            self.state
                .issue("handshake_not_observed", "snapshot", "session");
        }
        let c = &self.state.counters;
        if c.connection_resets > 0
            || c.skipped_gaps > 0
            || c.backlog_overflows > 0
            || c.truncated_messages > 0
            || c.decode_failures_after_handshake > 0
        {
            self.state
                .issue("transport_or_decode_incomplete", "snapshot", "transport");
        }
    }
}

#[derive(Debug, PartialEq, Eq)]
enum FrameFault {
    ShortHeader,
    BodyShortfall,
    LengthOverflow,
}

fn complete_message_frame(bytes: &[u8]) -> Result<(MessageHeader, usize), FrameFault> {
    let header = MessageHeader::decode(bytes).map_err(|_| FrameFault::ShortHeader)?;
    let body_length =
        usize::try_from(header.body_length).map_err(|_| FrameFault::LengthOverflow)?;
    let declared_end = usize::from(header.head_length)
        .checked_add(body_length)
        .and_then(|length| length.checked_add(MESSAGE_HEADER_SIZE))
        .ok_or(FrameFault::LengthOverflow)?;
    if declared_end > bytes.len() {
        return Err(FrameFault::BodyShortfall);
    }
    Ok((header, declared_end))
}

#[cfg(test)]
mod tests {
    use super::*;
    use zzz_wire::proto::{Field, Value};

    fn data() -> GameData {
        crate::protocol::load_protocol(
            &std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".upstream/assets"),
        )
        .unwrap()
    }

    #[test]
    fn failed_response_cannot_become_a_successful_empty_inventory() {
        let data = data();
        let mut pipeline = Pipeline::new("synthetic".into(), 1, &data);
        for (command, field) in [(3933, 12), (1382, 12), (2470, 8)] {
            pipeline.accept_message(
                command,
                &Message {
                    fields: vec![Field::new(field, Value::Varint(1))],
                },
            );
        }
        assert!(!pipeline.state.coverage.disc_load_received);
        assert!(!pipeline.state.coverage.engine_load_received);
        assert!(!pipeline.state.coverage.agent_load_received);
        assert_eq!(pipeline.state.response_observations.len(), 3);
        assert_eq!(pipeline.state.issues.len(), 3);
        assert!(pipeline.state.assets.discs.is_empty());
    }

    #[test]
    fn zero_and_omitted_status_allow_observation_but_do_not_prove_status_semantics() {
        let data = data();
        let mut zero = Pipeline::new("synthetic".into(), 1, &data);
        let mut omitted = Pipeline::new("synthetic".into(), 1, &data);
        for (command, field) in [(3933, 12), (1382, 12), (2470, 8)] {
            zero.accept_message(
                command,
                &Message {
                    fields: vec![Field::new(field, Value::Varint(0))],
                },
            );
            omitted.accept_message(command, &Message::default());
        }
        assert!(zero.state.coverage.all_received());
        assert!(omitted.state.coverage.all_received());
        assert!(zero.state.issues.is_empty());
        assert!(omitted.state.issues.is_empty());
        assert!(zero
            .state
            .response_observations
            .iter()
            .all(|r| r.status_value == Some(0) && !r.status_semantics_verified));
        assert!(omitted
            .state
            .response_observations
            .iter()
            .all(|r| r.status_presence == crate::types::Presence::Omitted
                && !r.status_semantics_verified));
    }

    #[test]
    fn malformed_status_is_rejected_and_incremental_unknown_uids_do_not_delete_records() {
        let data = data();
        let mut pipeline = Pipeline::new("synthetic".into(), 1, &data);
        pipeline.accept_message(
            3933,
            &Message {
                fields: vec![Field::new(12, Value::LengthDelimited(vec![]))],
            },
        );
        assert!(!pipeline.state.coverage.disc_load_received);
        pipeline.accept_message(
            data.datamine.cmd_player_sync_sc_notify as u16,
            &Message::default(),
        );
        pipeline.accept_message(
            data.datamine.cmd_dismantle_equip_cs_req as u16,
            &Message::default(),
        );
        assert_eq!(pipeline.state.counters.incremental_messages_ignored, 2);
    }

    #[test]
    fn short_declared_bodies_are_rejected_but_upstream_trailing_bytes_are_allowed() {
        let header = MessageHeader {
            command_id: 1,
            head_length: 0,
            body_length: 3,
            ..Default::default()
        };
        let mut bytes = header.encode().to_vec();
        assert_eq!(
            complete_message_frame(&bytes),
            Err(FrameFault::BodyShortfall)
        );
        bytes.extend_from_slice(&[1, 2, 3]);
        let (_, declared_end) = complete_message_frame(&bytes).unwrap();
        assert_eq!(declared_end, bytes.len());
        bytes.push(4);
        assert_eq!(complete_message_frame(&bytes).unwrap().1, declared_end);
        assert_eq!(
            complete_message_frame(&bytes[..11]),
            Err(FrameFault::ShortHeader)
        );
    }
}
