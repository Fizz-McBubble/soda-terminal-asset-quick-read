use super::*;
use crate::{
    observations::parse_load_response_with_potential,
    types::{AssetKind, LoadRecords, Presence},
};
use zzz_wire::{
    proto::{Field, Message, Value},
    ProtoEntry, ProtoEntryField, Protonap,
};

fn data() -> GameData {
    crate::protocol::load_protocol(
        &std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".upstream/assets"),
    )
    .unwrap()
}

fn legacy_32_data() -> GameData {
    let mut data = data();
    data.datamine.cmd_get_avatar_data_sc_rsp = 2470;
    data.datamine.agent_data.agents = 2;
    data.proto_entries
        .iter_mut()
        .filter(|row| row.cmd_id == Some(7398))
        .for_each(|row| row.cmd_id = Some(2470));
    data.proto_entries
        .iter_mut()
        .filter(|row| row.cmd_id == Some(2470))
        .flat_map(|row| row.fields.iter_mut())
        .filter(|field| field.type_name == "CCLLIEJENHM")
        .for_each(|field| {
            field.number = 2;
            field.type_name = "OCNCGOBPHLA".into();
        });
    data.proto_entries
        .iter_mut()
        .filter(|row| row.cmd_id == Some(2470))
        .flat_map(|row| row.fields.iter_mut())
        .filter(|field| field.name == "BIGOOGEGJEL")
        .for_each(|field| {
            field.number = 8;
            field.name = "OIOKFJKBLBG".into();
        });
    data.proto_entries.push(ProtoEntry {
        cmd_id: None,
        name: "OCNCGOBPHLA".into(),
        fields: vec![ProtoEntryField {
            number: 665,
            name: "ENNAMBHGLGD".into(),
            type_name: "uint32".into(),
            xor_value: Some(1715),
            is_native_type: true,
            is_enum: false,
            repeated: false,
        }],
    });
    data.nap = Protonap::new(data.proto_entries.clone());
    data
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
fn reviewed_33_has_no_verified_potential_mapping_and_never_defaults_to_zero() {
    let data = data();
    assert_eq!(
        potential_mapping(&data),
        PotentialMapping::ReviewedUnavailable
    );
    assert_eq!(potential_mapping(&data).field(), None);
    let row = agent(&data, Some(Value::Varint(121104)), None);
    assert_eq!(row.catalog_id.value, Some(1211));
    assert!(row.potential_detail_id.is_none());
    let rendered = serde_json::to_string(&row).unwrap();
    assert!(!rendered.contains("potential_detail_id"));
}

#[test]
fn pinned_32_mapping_remains_exact_without_being_inferred_for_33() {
    let legacy = legacy_32_data();
    assert_eq!(potential_mapping(&legacy), PotentialMapping::Verified(665));
    for change in 0..4 {
        let mut changed = legacy_32_data();
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
        assert_eq!(potential_mapping(&changed), PotentialMapping::Unsupported);
    }
}

#[test]
fn legacy_observed_and_invalid_details_preserve_presence_without_becoming_levels() {
    let data = legacy_32_data();
    let field = potential_mapping(&data).field();
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
    for value in [Value::LengthDelimited(vec![1]), Value::Varint(u64::MAX)] {
        let row = agent(&data, Some(value), field);
        assert_eq!(row.catalog_id.value, Some(1211));
        let observation = row.potential_detail_id.unwrap();
        assert_eq!(observation.presence, Presence::Invalid);
        assert_eq!(observation.value, None);
    }
}

#[test]
fn drift_in_the_33_agent_schema_cannot_enable_a_guessed_field() {
    let mut changed = data();
    let schema = changed
        .proto_entries
        .iter_mut()
        .find(|row| row.name == "CCLLIEJENHM")
        .unwrap();
    schema.fields.push(ProtoEntryField {
        number: 1993,
        name: "ENNAMBHGLGD".into(),
        type_name: "uint32".into(),
        xor_value: Some(1715),
        is_native_type: true,
        is_enum: false,
        repeated: false,
    });
    changed.nap = Protonap::new(changed.proto_entries.clone());
    assert_eq!(potential_mapping(&changed), PotentialMapping::Unsupported);
    assert_eq!(potential_mapping(&changed).field(), None);
}

#[test]
fn missing_or_duplicate_33_agent_container_is_unsupported_without_panicking() {
    for duplicate in [false, true] {
        let mut changed = data();
        let response = changed
            .proto_entries
            .iter_mut()
            .find(|row| row.cmd_id == Some(7398))
            .unwrap();
        if duplicate {
            let container = response
                .fields
                .iter()
                .find(|field| field.number == 12)
                .unwrap()
                .clone();
            response.fields.push(container);
        } else {
            response.fields.retain(|field| field.number != 12);
        }
        assert_eq!(potential_mapping(&changed), PotentialMapping::Unsupported);
    }
}
