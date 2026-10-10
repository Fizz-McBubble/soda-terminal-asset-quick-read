use crate::{snapshot::SnapshotState, types::ScalarObservation};
use std::collections::BTreeSet;

fn required_id(value: &ScalarObservation) -> Option<u32> {
    value.value.filter(|value| *value != 0)
}

pub fn validate_snapshot(state: &mut SnapshotState) {
    let mut disc_ids = BTreeSet::new();
    let mut engine_ids = BTreeSet::new();
    let mut agent_ids = BTreeSet::new();
    let mut issues = Vec::new();
    for disc in &state.assets.discs {
        match required_id(&disc.uid) {
            Some(uid) if disc_ids.insert(uid) => {}
            _ => issues.push(("missing_or_duplicate_instance_id", "disc", "uid")),
        }
        if required_id(&disc.catalog_id).is_none() {
            issues.push(("missing_catalog_id", "disc", "catalog_id"));
        }
        if disc
            .catalog_id
            .value
            .is_some_and(|id| !(1..=6).contains(&(id % 10)))
        {
            issues.push(("invalid_slot", "disc", "catalog_id"));
        }
        if disc.level.value.is_some_and(|level| level > 15) {
            issues.push(("out_of_range", "disc", "level"));
        }
        if disc.substats.len() > 4 {
            issues.push(("too_many_substats", "disc", "substats"));
        }
    }
    for engine in &state.assets.engines {
        match required_id(&engine.uid) {
            Some(uid) if engine_ids.insert(uid) => {}
            _ => issues.push(("missing_or_duplicate_instance_id", "engine", "uid")),
        }
        if required_id(&engine.catalog_id).is_none() {
            issues.push(("missing_catalog_id", "engine", "catalog_id"));
        }
        if engine.level.value.is_some_and(|level| level > 60) {
            issues.push(("out_of_range", "engine", "level"));
        }
    }
    let mut worn_discs = BTreeSet::new();
    let mut worn_engines = BTreeSet::new();
    for agent in &state.assets.agents {
        match required_id(&agent.catalog_id) {
            Some(id) if agent_ids.insert(id) => {}
            _ => issues.push(("missing_or_duplicate_agent_id", "agent", "catalog_id")),
        }
        if agent.level.value.is_some_and(|level| level > 60)
            || agent.mindscape.value.is_some_and(|value| value > 6)
        {
            issues.push(("out_of_range", "agent", "progression"));
        }
        if let Some(uid) = required_id(&agent.weapon_uid) {
            if !engine_ids.contains(&uid) || !worn_engines.insert(uid) {
                issues.push(("missing_or_shared_engine_reference", "agent", "weapon_uid"));
            }
        }
        let mut slots = BTreeSet::new();
        for equip in &agent.dressed_equips {
            let uid = required_id(&equip.uid);
            if uid.is_none_or(|id| !disc_ids.contains(&id) || !worn_discs.insert(id)) {
                issues.push((
                    "missing_or_shared_disc_reference",
                    "agent",
                    "dressed_equips",
                ));
            }
            if equip
                .slot
                .value
                .is_none_or(|slot| !(1..=6).contains(&slot) || !slots.insert(slot))
            {
                issues.push((
                    "invalid_or_duplicate_equipped_slot",
                    "agent",
                    "dressed_equips",
                ));
            }
            if let (Some(uid), Some(slot)) = (uid, equip.slot.value) {
                if state
                    .assets
                    .discs
                    .iter()
                    .find(|disc| disc.uid.value == Some(uid))
                    .and_then(|disc| disc.catalog_id.value)
                    .is_some_and(|id| id % 10 != slot)
                {
                    issues.push(("equipped_slot_mismatch", "agent", "dressed_equips"));
                }
            }
        }
    }
    for (code, asset, field) in issues {
        state.issue(code, asset, field);
    }
}
