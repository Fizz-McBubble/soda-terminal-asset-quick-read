use super::*;

#[test]
fn singular_message_occurrences_merge_fields_without_hiding_malformed_fragments() {
    let message = Message {
        fields: vec![
            Field::new(
                7,
                Value::LengthDelimited(
                    Message {
                        fields: vec![
                            Field::new(1, Value::Varint(11)),
                            Field::new(2, Value::Varint(20)),
                        ],
                    }
                    .encode(),
                ),
            ),
            Field::new(
                7,
                Value::LengthDelimited(
                    Message {
                        fields: vec![Field::new(2, Value::Varint(30))],
                    }
                    .encode(),
                ),
            ),
        ],
    };
    let mut issues = Vec::new();
    let merged = parse_singular_nested(
        &message,
        7,
        &mut issues,
        "disc",
        "main_stat",
        |message, issues| {
            (
                parse_scalar(message, 1, issues, "disc", "key"),
                parse_scalar(message, 2, issues, "disc", "base_value"),
            )
        },
    )
    .unwrap();
    assert_eq!(merged.0.value, Some(11));
    assert_eq!(merged.1.value, Some(30));
    assert!(issues.is_empty());
    let mut malformed = message;
    malformed
        .fields
        .push(Field::new(7, Value::LengthDelimited(vec![0x80])));
    parse_singular_nested(&malformed, 7, &mut issues, "disc", "main_stat", |_, _| ()).unwrap();
    assert!(issues.iter().any(|issue| issue.code == "malformed_nested"));
}
use zzz_wire::proto::{Field, Message, Value};

fn committed_datamine() -> Datamine {
    Datamine::parse(include_str!("../.upstream/assets/datamine.json"))
        .expect("committed datamine.json should parse")
}

#[test]
fn test_omitted_vs_observed_zero() {
    let datamine = committed_datamine();

    // 1. Disc: level is observed 0, main_stat and substats omitted
    let disc_msg = Message {
        fields: vec![
            Field::new(datamine.disc_info.uid, Value::Varint(101)),
            Field::new(datamine.disc_info.id, Value::Varint(201)),
            Field::new(datamine.disc_info.level, Value::Varint(0)), // observed zero
        ],
    };

    let response_msg = Message {
        fields: vec![Field::new(
            datamine.equip_data.discs,
            Value::LengthDelimited(disc_msg.encode()),
        )],
    };

    let result = parse_load_response(AssetKind::Discs, &response_msg, &datamine)
        .expect("valid disc response");
    match result {
        LoadRecords::Discs(discs) => {
            assert_eq!(discs.len(), 1);
            let disc = &discs[0];
            assert_eq!(disc.level.presence, Presence::Present);
            assert_eq!(disc.level.value, Some(0));
            assert_eq!(disc.uid.presence, Presence::Present);
            assert_eq!(disc.uid.value, Some(101));
            assert_eq!(disc.catalog_id.presence, Presence::Present);
            assert_eq!(disc.catalog_id.value, Some(201));
            assert!(disc.main_stat.is_none());
            assert!(disc.substats.is_empty());
        }
        _ => panic!("expected Discs"),
    }

    // 2. Engine: level omitted, phase observed 0, modification omitted
    let engine_msg = Message {
        fields: vec![
            Field::new(datamine.weapon_info.uid, Value::Varint(301)),
            Field::new(datamine.weapon_info.id, Value::Varint(401)),
            // level omitted!
            Field::new(datamine.weapon_info.phase, Value::Varint(0)), // observed zero
                                                                      // modification omitted!
        ],
    };

    let engine_response = Message {
        fields: vec![Field::new(
            datamine.weapon_data.weapons,
            Value::LengthDelimited(engine_msg.encode()),
        )],
    };

    let engine_result = parse_load_response(AssetKind::Engines, &engine_response, &datamine)
        .expect("valid engine response");
    match engine_result {
        LoadRecords::Engines(engines) => {
            assert_eq!(engines.len(), 1);
            let engine = &engines[0];
            assert_eq!(engine.level.presence, Presence::Omitted);
            assert_eq!(engine.level.value, None);
            assert_eq!(engine.phase.presence, Presence::Present);
            assert_eq!(engine.phase.value, Some(0));
            assert_eq!(engine.modification.presence, Presence::Omitted);
            assert_eq!(engine.modification.value, None);
        }
        _ => panic!("expected Engines"),
    }
}

#[test]
fn test_malformed_nested_payload() {
    let datamine = committed_datamine();

    // A disc containing malformed nested bytes in main_stat
    let disc_msg = Message {
        fields: vec![
            Field::new(datamine.disc_info.uid, Value::Varint(101)),
            Field::new(
                datamine.disc_info.main_stat,
                Value::LengthDelimited(vec![0xFF, 0xFF, 0xFF]), // truncated / malformed varint bytes
            ),
        ],
    };

    let response = Message {
        fields: vec![Field::new(
            datamine.equip_data.discs,
            Value::LengthDelimited(disc_msg.encode()),
        )],
    };

    let err = parse_load_response(AssetKind::Discs, &response, &datamine).unwrap_err();
    assert!(
        err.iter().any(|issue| issue.code == "malformed_nested"
            && issue.asset == "disc"
            && issue.field == "main_stat"),
        "expected malformed_nested issue, got: {err:?}"
    );
}

#[test]
fn test_wrong_scalar_type() {
    let datamine = committed_datamine();

    // Disc with length-delimited string where Varint level was expected
    let disc_msg = Message {
        fields: vec![
            Field::new(datamine.disc_info.uid, Value::Varint(101)),
            Field::new(
                datamine.disc_info.level,
                Value::LengthDelimited(b"invalid_scalar".to_vec()),
            ),
        ],
    };

    let response = Message {
        fields: vec![Field::new(
            datamine.equip_data.discs,
            Value::LengthDelimited(disc_msg.encode()),
        )],
    };

    let err = parse_load_response(AssetKind::Discs, &response, &datamine).unwrap_err();
    assert!(
        err.iter().any(|issue| issue.code == "wrong_wire_type"
            && issue.asset == "disc"
            && issue.field == "level"),
        "expected wrong_wire_type issue, got: {err:?}"
    );
}

#[test]
fn test_u32_overflow() {
    let datamine = committed_datamine();

    // Engine with uid exceeding u32::MAX
    let engine_msg = Message {
        fields: vec![
            Field::new(
                datamine.weapon_info.uid,
                Value::Varint((u32::MAX as u64) + 42),
            ),
            Field::new(datamine.weapon_info.id, Value::Varint(100)),
        ],
    };

    let response = Message {
        fields: vec![Field::new(
            datamine.weapon_data.weapons,
            Value::LengthDelimited(engine_msg.encode()),
        )],
    };

    let err = parse_load_response(AssetKind::Engines, &response, &datamine).unwrap_err();
    assert!(
        err.iter().any(|issue| issue.code == "u32_overflow"
            && issue.asset == "engine"
            && issue.field == "uid"),
        "expected u32_overflow issue, got: {err:?}"
    );
}

#[test]
fn test_duplicate_physical_discs_with_same_properties_preserving_different_uid() {
    let datamine = committed_datamine();

    let stat_msg = Message {
        fields: vec![
            Field::new(datamine.disc_stat.key, Value::Varint(11102)),
            Field::new(datamine.disc_stat.base_value, Value::Varint(100)),
            Field::new(datamine.disc_stat.add_value, Value::Varint(0)),
        ],
    };

    // Disc 1
    let disc1 = Message {
        fields: vec![
            Field::new(datamine.disc_info.uid, Value::Varint(8881)),
            Field::new(datamine.disc_info.id, Value::Varint(5001)),
            Field::new(datamine.disc_info.level, Value::Varint(15)),
            Field::new(
                datamine.disc_info.main_stat,
                Value::LengthDelimited(stat_msg.encode()),
            ),
        ],
    };

    // Disc 2: identical catalog_id, level, main_stat, but distinct physical uid
    let disc2 = Message {
        fields: vec![
            Field::new(datamine.disc_info.uid, Value::Varint(8882)),
            Field::new(datamine.disc_info.id, Value::Varint(5001)),
            Field::new(datamine.disc_info.level, Value::Varint(15)),
            Field::new(
                datamine.disc_info.main_stat,
                Value::LengthDelimited(stat_msg.encode()),
            ),
        ],
    };

    let response = Message {
        fields: vec![
            Field::new(
                datamine.equip_data.discs,
                Value::LengthDelimited(disc1.encode()),
            ),
            Field::new(
                datamine.equip_data.discs,
                Value::LengthDelimited(disc2.encode()),
            ),
        ],
    };

    let result = parse_load_response(AssetKind::Discs, &response, &datamine)
        .expect("should preserve both discs");
    match result {
        LoadRecords::Discs(discs) => {
            assert_eq!(discs.len(), 2, "both physical discs must be preserved");
            assert_eq!(discs[0].uid.value, Some(8881));
            assert_eq!(discs[1].uid.value, Some(8882));
            assert_eq!(discs[0].catalog_id.value, Some(5001));
            assert_eq!(discs[1].catalog_id.value, Some(5001));
        }
        _ => panic!("expected Discs"),
    }
}

#[test]
fn test_empty_three_response_kinds() {
    let datamine = committed_datamine();
    let empty_msg = Message::default();

    let discs = parse_load_response(AssetKind::Discs, &empty_msg, &datamine).expect("empty discs");
    match discs {
        LoadRecords::Discs(records) => assert!(records.is_empty()),
        _ => panic!("expected Discs"),
    }

    let engines =
        parse_load_response(AssetKind::Engines, &empty_msg, &datamine).expect("empty engines");
    match engines {
        LoadRecords::Engines(records) => assert!(records.is_empty()),
        _ => panic!("expected Engines"),
    }

    let agents =
        parse_load_response(AssetKind::Agents, &empty_msg, &datamine).expect("empty agents");
    match agents {
        LoadRecords::Agents(records) => assert!(records.is_empty()),
        _ => panic!("expected Agents"),
    }
}

#[test]
fn test_agent_skill_type_and_engine_equip_associations() {
    let datamine = committed_datamine();

    // Skill 1: skill_type 3, level 11
    let skill1 = Message {
        fields: vec![
            Field::new(datamine.agent_skill.skill_type, Value::Varint(3)),
            Field::new(datamine.agent_skill.level, Value::Varint(11)),
        ],
    };

    // Skill 2: skill_type 1, level 7
    let skill2 = Message {
        fields: vec![
            Field::new(datamine.agent_skill.skill_type, Value::Varint(1)),
            Field::new(datamine.agent_skill.level, Value::Varint(7)),
        ],
    };

    // Dressed Equip 1: uid 7001, slot 2
    let equip1 = Message {
        fields: vec![
            Field::new(datamine.agent_equip.uid, Value::Varint(7001)),
            Field::new(datamine.agent_equip.slot, Value::Varint(2)),
        ],
    };

    // Agent message
    let agent_msg = Message {
        fields: vec![
            Field::new(datamine.agent_info.id, Value::Varint(1011)),
            Field::new(datamine.agent_info.level, Value::Varint(50)),
            Field::new(datamine.agent_info.promotion, Value::Varint(4)),
            Field::new(datamine.agent_info.weapon_uid, Value::Varint(9099)),
            Field::new(datamine.agent_info.mindscape, Value::Varint(1)),
            Field::new(
                datamine.agent_info.skills,
                Value::LengthDelimited(skill1.encode()),
            ),
            Field::new(
                datamine.agent_info.skills,
                Value::LengthDelimited(skill2.encode()),
            ),
            Field::new(
                datamine.agent_info.dressed_equips,
                Value::LengthDelimited(equip1.encode()),
            ),
        ],
    };

    let response = Message {
        fields: vec![Field::new(
            datamine.agent_data.agents,
            Value::LengthDelimited(agent_msg.encode()),
        )],
    };

    let result =
        parse_load_response(AssetKind::Agents, &response, &datamine).expect("valid agent response");
    match result {
        LoadRecords::Agents(agents) => {
            assert_eq!(agents.len(), 1);
            let agent = &agents[0];
            assert_eq!(agent.catalog_id.value, Some(1011));
            assert_eq!(agent.weapon_uid.value, Some(9099));

            // Skills retain explicit skill_type, not positional assumption
            assert_eq!(agent.skills.len(), 2);
            assert_eq!(agent.skills[0].skill_type.value, Some(3));
            assert_eq!(agent.skills[0].level.value, Some(11));
            assert_eq!(agent.skills[1].skill_type.value, Some(1));
            assert_eq!(agent.skills[1].level.value, Some(7));

            // Dressed equip association
            assert_eq!(agent.dressed_equips.len(), 1);
            assert_eq!(agent.dressed_equips[0].uid.value, Some(7001));
            assert_eq!(agent.dressed_equips[0].slot.value, Some(2));
        }
        _ => panic!("expected Agents"),
    }
}

#[test]
fn test_valid_singular_repetition_last_occurrence_semantics() {
    let datamine = committed_datamine();

    // Level appears twice: 5, then 15. Last occurrence 15 must win without errors.
    let disc_msg = Message {
        fields: vec![
            Field::new(datamine.disc_info.uid, Value::Varint(101)),
            Field::new(datamine.disc_info.id, Value::Varint(201)),
            Field::new(datamine.disc_info.level, Value::Varint(5)),
            Field::new(datamine.disc_info.level, Value::Varint(15)),
        ],
    };

    let response = Message {
        fields: vec![Field::new(
            datamine.equip_data.discs,
            Value::LengthDelimited(disc_msg.encode()),
        )],
    };

    let result = parse_load_response(AssetKind::Discs, &response, &datamine)
        .expect("valid repetition with last occurrence");
    match result {
        LoadRecords::Discs(discs) => {
            assert_eq!(discs.len(), 1);
            assert_eq!(discs[0].level.value, Some(15));
            assert_eq!(discs[0].level.presence, Presence::Present);
        }
        _ => panic!("expected Discs"),
    }
}

#[test]
fn test_diagnostic_accumulation_capped_at_100() {
    let datamine = committed_datamine();

    // Create a response with 150 malformed items
    let mut fields = Vec::with_capacity(150);
    for _ in 0..150 {
        fields.push(Field::new(
            datamine.equip_data.discs,
            Value::LengthDelimited(vec![0xFF, 0xFF, 0xFF]), // malformed protobuf bytes
        ));
    }
    let response = Message { fields };

    let issues = parse_load_response(AssetKind::Discs, &response, &datamine).unwrap_err();
    assert_eq!(
        issues.len(),
        100,
        "diagnostic accumulation must be capped at 100"
    );
}
