use crate::types::{
    AgentRecord, AssetKind, DecodeIssue, DiscRecord, EngineRecord, EquipRecord, LoadRecords,
    Presence, ScalarObservation, SkillRecord, StatRecord,
};
use zzz_gamedata::Datamine;
use zzz_wire::proto::{Field, Message, Value};

/// Maximum number of diagnostic issues accumulated per load response to bound allocation.
const MAX_DIAGNOSTIC_ISSUES: usize = 100;

fn record_issue(issues: &mut Vec<DecodeIssue>, code: &str, asset: &str, field: &str) {
    if issues.len() < MAX_DIAGNOSTIC_ISSUES {
        issues.push(DecodeIssue {
            code: code.to_string(),
            asset: asset.to_string(),
            field: field.to_string(),
        });
    }
}

/// Reads a singular scalar u32 field from a protobuf message.
///
/// Protobuf singular fields follow last-occurrence semantics.
/// - If omitted: presence is Omitted, value is None.
/// - If present and valid: presence is Present, value is Some(u32).
/// - If wire type is not Varint or value overflows u32: presence is Invalid, value is None,
///   and a concise DecodeIssue is recorded without payload logging.
fn parse_scalar(
    message: &Message,
    field_number: u32,
    issues: &mut Vec<DecodeIssue>,
    asset: &str,
    field_name: &str,
) -> ScalarObservation {
    let matching: Vec<&Field> = message
        .fields
        .iter()
        .filter(|f| f.number == field_number)
        .collect();

    if matching.is_empty() {
        return ScalarObservation {
            value: None,
            presence: Presence::Omitted,
        };
    }

    // Check all occurrences for wire errors or overflow
    for field in &matching {
        match &field.value {
            Value::Varint(v) => {
                if *v > u32::MAX as u64 {
                    record_issue(issues, "u32_overflow", asset, field_name);
                }
            }
            _ => {
                record_issue(issues, "wrong_wire_type", asset, field_name);
            }
        }
    }

    // Last occurrence semantics
    let last = matching.last().unwrap();
    match &last.value {
        Value::Varint(v) => match u32::try_from(*v) {
            Ok(val) => ScalarObservation {
                value: Some(val),
                presence: Presence::Present,
            },
            Err(_) => ScalarObservation {
                value: None,
                presence: Presence::Invalid,
            },
        },
        _ => ScalarObservation {
            value: None,
            presence: Presence::Invalid,
        },
    }
}

/// Helper to parse repeated nested messages with strict LengthDelimited wire checking,
/// strict Message::decode parsing, and concise issue reporting without unwrap_or_default.
fn parse_repeated_nested<T, F>(
    message: &Message,
    field_number: u32,
    issues: &mut Vec<DecodeIssue>,
    asset: &str,
    field_name: &str,
    mut parse_item: F,
) -> Vec<T>
where
    F: FnMut(&Message, &mut Vec<DecodeIssue>) -> T,
{
    let mut items = Vec::new();
    for field in message.fields.iter().filter(|f| f.number == field_number) {
        match &field.value {
            Value::LengthDelimited(bytes) => match Message::decode(bytes) {
                Ok(item_msg) => {
                    items.push(parse_item(&item_msg, issues));
                }
                Err(_) => {
                    record_issue(issues, "malformed_nested", asset, field_name);
                }
            },
            _ => {
                record_issue(issues, "wrong_wire_type", asset, field_name);
            }
        }
    }
    items
}

/// Protobuf merges repeated occurrences of a singular message. Its inner scalars
/// still use last-occurrence semantics; malformed occurrences remain diagnostic.
fn parse_singular_nested<T, F>(
    message: &Message,
    field_number: u32,
    issues: &mut Vec<DecodeIssue>,
    asset: &str,
    field_name: &str,
    parse_item: F,
) -> Option<T>
where
    F: FnOnce(&Message, &mut Vec<DecodeIssue>) -> T,
{
    let mut merged = Message::default();
    let mut observed = false;
    for field in message.fields.iter().filter(|f| f.number == field_number) {
        match &field.value {
            Value::LengthDelimited(bytes) => match Message::decode(bytes) {
                Ok(decoded) => {
                    observed = true;
                    merged.fields.extend(decoded.fields);
                }
                Err(_) => record_issue(issues, "malformed_nested", asset, field_name),
            },
            _ => {
                record_issue(issues, "wrong_wire_type", asset, field_name);
            }
        }
    }

    observed.then(|| parse_item(&merged, issues))
}

fn parse_stat_record(
    message: &Message,
    datamine: &Datamine,
    issues: &mut Vec<DecodeIssue>,
    asset: &str,
    prefix: &str,
) -> StatRecord {
    let key = parse_scalar(
        message,
        datamine.disc_stat.key,
        issues,
        asset,
        &format!("{prefix}.key"),
    );
    let base_value = parse_scalar(
        message,
        datamine.disc_stat.base_value,
        issues,
        asset,
        &format!("{prefix}.base_value"),
    );
    let add_value = parse_scalar(
        message,
        datamine.disc_stat.add_value,
        issues,
        asset,
        &format!("{prefix}.add_value"),
    );
    StatRecord {
        key,
        base_value,
        add_value,
    }
}

fn parse_disc_record(
    message: &Message,
    datamine: &Datamine,
    issues: &mut Vec<DecodeIssue>,
) -> DiscRecord {
    let uid = parse_scalar(message, datamine.disc_info.uid, issues, "disc", "uid");
    let catalog_id = parse_scalar(message, datamine.disc_info.id, issues, "disc", "catalog_id");
    let level = parse_scalar(message, datamine.disc_info.level, issues, "disc", "level");

    let main_stat = parse_singular_nested(
        message,
        datamine.disc_info.main_stat,
        issues,
        "disc",
        "main_stat",
        |m, issues| parse_stat_record(m, datamine, issues, "disc", "main_stat"),
    );

    let substats = parse_repeated_nested(
        message,
        datamine.disc_info.sub_stats,
        issues,
        "disc",
        "substats",
        |m, issues| parse_stat_record(m, datamine, issues, "disc", "substats"),
    );

    DiscRecord {
        uid,
        catalog_id,
        level,
        main_stat,
        substats,
    }
}

fn parse_engine_record(
    message: &Message,
    datamine: &Datamine,
    issues: &mut Vec<DecodeIssue>,
) -> EngineRecord {
    let uid = parse_scalar(message, datamine.weapon_info.uid, issues, "engine", "uid");
    let catalog_id = parse_scalar(
        message,
        datamine.weapon_info.id,
        issues,
        "engine",
        "catalog_id",
    );
    let level = parse_scalar(
        message,
        datamine.weapon_info.level,
        issues,
        "engine",
        "level",
    );
    let phase = parse_scalar(
        message,
        datamine.weapon_info.phase,
        issues,
        "engine",
        "phase",
    );
    let modification = parse_scalar(
        message,
        datamine.weapon_info.modification,
        issues,
        "engine",
        "modification",
    );

    EngineRecord {
        uid,
        catalog_id,
        level,
        phase,
        modification,
    }
}

fn parse_skill_record(
    message: &Message,
    datamine: &Datamine,
    issues: &mut Vec<DecodeIssue>,
) -> SkillRecord {
    let skill_type = parse_scalar(
        message,
        datamine.agent_skill.skill_type,
        issues,
        "agent",
        "skills.skill_type",
    );
    let level = parse_scalar(
        message,
        datamine.agent_skill.level,
        issues,
        "agent",
        "skills.level",
    );
    SkillRecord { skill_type, level }
}

fn parse_equip_record(
    message: &Message,
    datamine: &Datamine,
    issues: &mut Vec<DecodeIssue>,
) -> EquipRecord {
    let uid = parse_scalar(
        message,
        datamine.agent_equip.uid,
        issues,
        "agent",
        "dressed_equips.uid",
    );
    let slot = parse_scalar(
        message,
        datamine.agent_equip.slot,
        issues,
        "agent",
        "dressed_equips.slot",
    );
    EquipRecord { uid, slot }
}

fn parse_agent_record(
    message: &Message,
    datamine: &Datamine,
    issues: &mut Vec<DecodeIssue>,
) -> AgentRecord {
    let catalog_id = parse_scalar(
        message,
        datamine.agent_info.id,
        issues,
        "agent",
        "catalog_id",
    );
    let level = parse_scalar(message, datamine.agent_info.level, issues, "agent", "level");
    let promotion = parse_scalar(
        message,
        datamine.agent_info.promotion,
        issues,
        "agent",
        "promotion",
    );
    let weapon_uid = parse_scalar(
        message,
        datamine.agent_info.weapon_uid,
        issues,
        "agent",
        "weapon_uid",
    );
    let mindscape = parse_scalar(
        message,
        datamine.agent_info.mindscape,
        issues,
        "agent",
        "mindscape",
    );

    let skills = parse_repeated_nested(
        message,
        datamine.agent_info.skills,
        issues,
        "agent",
        "skills",
        |m, issues| parse_skill_record(m, datamine, issues),
    );

    let dressed_equips = parse_repeated_nested(
        message,
        datamine.agent_info.dressed_equips,
        issues,
        "agent",
        "dressed_equips",
        |m, issues| parse_equip_record(m, datamine, issues),
    );

    AgentRecord {
        catalog_id,
        level,
        promotion,
        weapon_uid,
        mindscape,
        skills,
        dressed_equips,
    }
}

/// Parses raw generic protobuf load responses into domain records using pinned datamine field numbers.
pub fn parse_load_response(
    kind: AssetKind,
    message: &Message,
    datamine: &Datamine,
) -> Result<LoadRecords, Vec<DecodeIssue>> {
    let mut issues = Vec::new();

    let records = match kind {
        AssetKind::Discs => {
            let discs = parse_repeated_nested(
                message,
                datamine.equip_data.discs,
                &mut issues,
                "disc",
                "item",
                |m, issues| parse_disc_record(m, datamine, issues),
            );
            LoadRecords::Discs(discs)
        }
        AssetKind::Engines => {
            let engines = parse_repeated_nested(
                message,
                datamine.weapon_data.weapons,
                &mut issues,
                "engine",
                "item",
                |m, issues| parse_engine_record(m, datamine, issues),
            );
            LoadRecords::Engines(engines)
        }
        AssetKind::Agents => {
            let agents = parse_repeated_nested(
                message,
                datamine.agent_data.agents,
                &mut issues,
                "agent",
                "item",
                |m, issues| parse_agent_record(m, datamine, issues),
            );
            LoadRecords::Agents(agents)
        }
    };

    if issues.is_empty() {
        Ok(records)
    } else {
        Err(issues)
    }
}

#[cfg(test)]
#[path = "observations_tests.rs"]
mod tests;
