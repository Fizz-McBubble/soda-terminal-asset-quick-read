use crate::types::{AssetKind, Presence};
use serde::Serialize;
use zzz_wire::{
    proto::{Message, Value},
    protonap::Protonap,
};

#[derive(Debug, Clone, Serialize)]
pub struct ResponseObservation {
    pub kind: AssetKind,
    pub command_id: u16,
    pub status_field: u32,
    pub status_value: Option<i32>,
    pub status_presence: Presence,
    pub assessment: &'static str,
    // The pinned descriptor proves the field layout, not its return-code meaning.
    pub status_semantics_verified: bool,
}

impl ResponseObservation {
    pub fn allows_asset_observation(&self) -> bool {
        matches!(
            self.assessment,
            "zero_semantics_unverified" | "omitted_semantics_unverified"
        )
    }
}

pub fn observe_response(
    kind: AssetKind,
    command_id: u16,
    message: &Message,
    nap: &Protonap,
) -> ResponseObservation {
    let (expected_command, number, xor) = match kind {
        AssetKind::Discs => (3933, 12, 10990),
        AssetKind::Engines => (1382, 12, 13563),
        AssetKind::Agents => (2470, 8, 14884),
    };
    let mut observation = ResponseObservation {
        kind,
        command_id,
        status_field: number,
        status_value: None,
        status_presence: Presence::Omitted,
        assessment: "omitted_semantics_unverified",
        status_semantics_verified: false,
    };
    let pinned = command_id == expected_command
        && nap.entry_by_cmd(command_id).is_some_and(|entry| {
            entry.fields.iter().any(|field| {
                field.number == number as i32
                    && field.name == "OIOKFJKBLBG"
                    && field.type_name == "int32"
                    && !field.repeated
                    && field.xor_value == Some(xor)
            })
        });
    if !pinned {
        observation.status_presence = Presence::Invalid;
        observation.assessment = "response_status_mapping_unverified";
        return observation;
    }
    for field in message.fields.iter().filter(|field| field.number == number) {
        let decoded = match field.value {
            Value::Varint(value) => i32::try_from(value)
                .ok()
                .or_else(|| i32::try_from(value as i64).ok()),
            _ => None,
        };
        let Some(value) = decoded else {
            observation.status_presence = Presence::Invalid;
            observation.status_value = None;
            observation.assessment = "invalid_response_status";
            return observation;
        };
        observation.status_presence = Presence::Present;
        observation.status_value = Some(value);
        observation.assessment = if value == 0 {
            "zero_semantics_unverified"
        } else {
            "nonzero_response_status"
        };
    }
    observation
}
