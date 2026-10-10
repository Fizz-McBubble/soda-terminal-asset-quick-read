use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum Presence {
    Present,
    Omitted,
    Invalid,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct ScalarObservation {
    pub value: Option<u32>,
    pub presence: Presence,
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum AssetKind {
    Discs,
    Engines,
    Agents,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct DecodeIssue {
    pub code: String,
    pub asset: String,
    pub field: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct StatRecord {
    pub key: ScalarObservation,
    pub base_value: ScalarObservation,
    pub add_value: ScalarObservation,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct DiscRecord {
    pub uid: ScalarObservation,
    pub catalog_id: ScalarObservation,
    pub level: ScalarObservation,
    pub main_stat: Option<StatRecord>,
    pub substats: Vec<StatRecord>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct EngineRecord {
    pub uid: ScalarObservation,
    pub catalog_id: ScalarObservation,
    pub level: ScalarObservation,
    pub phase: ScalarObservation,
    pub modification: ScalarObservation,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct SkillRecord {
    pub skill_type: ScalarObservation,
    pub level: ScalarObservation,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct EquipRecord {
    pub uid: ScalarObservation,
    pub slot: ScalarObservation,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct AgentRecord {
    pub catalog_id: ScalarObservation,
    pub level: ScalarObservation,
    pub promotion: ScalarObservation,
    pub weapon_uid: ScalarObservation,
    pub mindscape: ScalarObservation,
    pub skills: Vec<SkillRecord>,
    pub dressed_equips: Vec<EquipRecord>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(tag = "kind", content = "records", rename_all = "snake_case")]
pub enum LoadRecords {
    Discs(Vec<DiscRecord>),
    Engines(Vec<EngineRecord>),
    Agents(Vec<AgentRecord>),
}
