use super::*;
use crate::{
    observations::parse_load_response_with_potential,
    types::{AssetKind, LoadRecords, Presence},
};
use zzz_wire::proto::{Field, Message, Value};

fn data() -> GameData {
    crate::protocol::load_protocol(
        &std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".upstream/assets"),
    )
    .unwrap()
}

fn agent(data: &GameData, value: Option<Value>, field: Option<u32>) -> crate::types::AgentRecord {
    let mut fields = vec![Field::new(data.datamine.agent_info.id, Value::Varint(1211))];
    if let Some(value) = value {
        fields.push(Field::new(665, value));
    }
    let response = Message {
        fields: vec![Field::new(
            data.datamine.agent_data.agents,
            Value::LengthDelimited(Message { fields }.encode()),
        )],
    };
    let LoadRecords::Agents(mut rows) =
        parse_load_response_with_potential(AssetKind::Agents, &response, &data.datamine, field)
            .unwrap()
    else {
        panic!("agent rows expected")
    };
    rows.remove(0)
}

#[test]
fn pinned_schema_has_guarded_potential_id_and_schema_drift_disables_it() {
    let original = data();
    assert_eq!(verified_potential_field(&original), Some(665));
    for change in 0..4 {
        let mut changed = data();
        let field = changed
            .proto_entries
            .iter_mut()
            .find(|row| row.name == "OCNCGOBPHLA")
            .unwrap()
            .fields
            .iter_mut()
            .find(|field| field.number == 665)
            .unwrap();
        match change {
            0 => field.repeated = true,
            1 => field.xor_value = Some(1716),
            2 => field.type_name = "string".into(),
            _ => field.name = "different".into(),
        }
        assert_eq!(verified_potential_field(&changed), None);
    }
    let mut changed = data();
    changed.datamine.cmd_get_avatar_data_sc_rsp += 1;
    assert_eq!(verified_potential_field(&changed), None);
}

#[test]
fn detail_id_is_observed_without_conflating_it_with_level_or_defaulting_missing_to_zero() {
    let data = data();
    let field = verified_potential_field(&data);
    for id in [119105, 110105, 121104] {
        let observed = agent(&data, Some(Value::Varint(id)), field)
            .potential_detail_id
            .unwrap();
        assert_eq!(observed.value, Some(id as u32));
        assert_eq!(observed.presence, Presence::Present);
    }
    let missing = agent(&data, None, field).potential_detail_id.unwrap();
    assert_eq!(missing.presence, Presence::Omitted);
    assert_eq!(missing.value, None);
    assert!(agent(&data, Some(Value::Varint(121104)), None)
        .potential_detail_id
        .is_none());
}

#[test]
fn invalid_detail_does_not_discard_other_agent_facts() {
    let data = data();
    for value in [Value::LengthDelimited(vec![1]), Value::Varint(u64::MAX)] {
        let row = agent(&data, Some(value), verified_potential_field(&data));
        assert_eq!(row.catalog_id.value, Some(1211));
        let observation = row.potential_detail_id.unwrap();
        assert_eq!(observation.presence, Presence::Invalid);
        assert_eq!(observation.value, None);
    }
}
