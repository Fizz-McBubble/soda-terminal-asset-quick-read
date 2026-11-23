use crate::types::{AssetKind, Presence};
use serde::Serialize;
use zzz_gamedata::Datamine;
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
    datamine: &Datamine,
    nap: &Protonap,
) -> ResponseObservation {
    let expected_command = match kind {
        AssetKind::Discs => datamine.cmd_get_equip_data_sc_rsp,
        AssetKind::Engines => datamine.cmd_get_weapon_data_sc_rsp,
        AssetKind::Agents => datamine.cmd_get_avatar_data_sc_rsp,
    };
    let status_field =
        response_status_field(command_id, nap).filter(|_| command_id == expected_command as u16);
    let mut observation = ResponseObservation {
        kind,
        command_id,
        status_field: status_field.unwrap_or(0),
        status_value: None,
        status_presence: Presence::Omitted,
        assessment: "omitted_semantics_unverified",
        status_semantics_verified: false,
    };
    let Some(number) = status_field else {
        observation.status_presence = Presence::Invalid;
        observation.assessment = "response_status_mapping_unverified";
        return observation;
    };
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

fn response_status_field(command_id: u16, nap: &Protonap) -> Option<u32> {
    let entry = nap.entry_by_cmd(command_id)?;
    let mut matches = entry.fields.iter().filter(|field| {
        matches!(field.name.as_str(), "BIGOOGEGJEL" | "OIOKFJKBLBG")
            && field.type_name == "int32"
            && field.number >= 0
            && field.is_native_type
            && !field.is_enum
            && !field.repeated
            && field.xor_value.is_some()
    });
    let field = matches.next()?;
    matches.next().is_none().then_some(field.number as u32)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn data() -> zzz_gamedata::GameData {
        crate::protocol::load_protocol(
            &std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".upstream/assets"),
        )
        .unwrap()
    }

    #[test]
    fn status_mappings_are_derived_from_reviewed_33_descriptors() {
        let data = data();
        for (kind, command, field) in [
            (AssetKind::Discs, 1744, 6),
            (AssetKind::Engines, 8256, 3),
            (AssetKind::Agents, 7398, 2),
        ] {
            assert_eq!(
                response_status_field(command, &data.nap),
                Some(field),
                "{kind:?}"
            );
            let observed = observe_response(
                kind,
                command,
                &Message::default(),
                &data.datamine,
                &data.nap,
            );
            assert_eq!(observed.status_field, field);
            assert_eq!(observed.status_presence, Presence::Omitted);
            assert!(observed.allows_asset_observation());
        }
    }

    #[test]
    fn an_old_or_unreviewed_command_number_is_not_retargeted() {
        let data = data();
        let observed = observe_response(
            AssetKind::Agents,
            2470,
            &Message::default(),
            &data.datamine,
            &data.nap,
        );
        assert_eq!(observed.status_field, 0);
        assert_eq!(observed.assessment, "response_status_mapping_unverified");
        assert!(!observed.allows_asset_observation());
    }
}
