#!/usr/bin/env node

const fs = require("fs");
const path = require("path");

const hostedBgmCsvUrl = "https://raw.githubusercontent.com/ff-meli/OrchestrionPlugin/master/Data/xiv_bgm.csv";

function readArg(name) {
  const idx = process.argv.indexOf(name);
  if (idx === -1 || idx + 1 >= process.argv.length) return undefined;
  return process.argv[idx + 1];
}

const observationsPath = readArg("--observations");
const logdataPath = readArg("--logdata");
const outPath = readArg("--out") ?? logdataPath;

if (!observationsPath || !logdataPath) {
  console.error("Usage: node tools/merge-observations.js --observations <snapshot.json> --logdata <logdata.json> [--out <merged.json>]");
  process.exit(1);
}

main().catch(error => {
  console.error(error);
  process.exit(1);
});

async function main() {
  const snapshot = JSON.parse(fs.readFileSync(observationsPath, "utf8"));
  const logdata = JSON.parse(fs.readFileSync(logdataPath, "utf8"));
  const observations = snapshot.Observations ?? snapshot.observations ?? [];
  const jobs = snapshot.Klassen_und_Jobs ?? snapshot.klassen_und_jobs ?? {};
  const music = snapshot.Music ?? snapshot.music ?? [];
  const chatLines = snapshot.ChatLines ?? snapshot.chatLines ?? [];
  const hostedBgmNames = await loadHostedBgmNames();

  let added = 0;
  let skipped = 0;

  for (const item of observations) {
    const zone = item.TerritoryName ?? item.territoryName;
    const enemy = item.SourceName ?? item.sourceName;
    const actionHex = item.ActionIdHex ?? item.actionIdHex;
    const actionName = item.ActionName ?? item.actionName ?? "";
    const categoryId = item.ActionCategoryId ?? item.actionCategoryId ?? 0;
    const damageType = normalizeDamageType(item.DamageType ?? item.damageType ?? "");
    const element = normalizeElement(item.Element ?? item.element ?? "");
    const territoryNameResolved = item.TerritoryNameResolved ?? item.territoryNameResolved ?? true;
    const sourceDataId = item.SourceDataId ?? item.sourceDataId;
    const sourceBaseId = item.SourceBaseId ?? item.sourceBaseId;
    const battleNpcNameId = item.BattleNpcNameId ?? item.battleNpcNameId;
    const modelId = item.ModelId ?? item.modelId;
    const level = item.Level ?? item.level;

    if (!zone || !enemy || !actionHex || !territoryNameResolved || isUnresolvedRsvName(zone)) {
      skipped++;
      continue;
    }

    logdata[zone] ??= {};
    logdata[zone][enemy] ??= {};
    logdata[zone][enemy].skill ??= {};
    mergeEnemyIdentity(logdata[zone][enemy], { sourceDataId, sourceBaseId, battleNpcNameId, modelId, level });
    mergeHp(logdata[zone][enemy], item);

    if (!logdata[zone][enemy].skill[actionHex]) {
      logdata[zone][enemy].skill[actionHex] = {
        name: actionName,
        type_id: String(categoryId),
      };
      added++;
    } else {
      skipped++;
    }

    mergeSkillDetails(logdata[zone][enemy].skill[actionHex], item);
    if (damageType) logdata[zone][enemy].skill[actionHex].damage_type = damageType;
    if (element) logdata[zone][enemy].skill[actionHex].element = element;
    mergeEnemyStatuses(logdata[zone][enemy], item);
  }

  mergeJobs(logdata, jobs);
  mergeMusic(logdata, music, hostedBgmNames);
  mergeChatLines(logdata, chatLines);

  fs.mkdirSync(path.dirname(outPath), { recursive: true });
  fs.writeFileSync(outPath, JSON.stringify(logdata), "utf8");

  console.log(`Added ${added} skill entries. Skipped ${skipped}. Wrote ${outPath}.`);
}

function isUnresolvedRsvName(value) {
  const text = String(value ?? "").trim().toLowerCase();
  return !text || text.startsWith("rsv_") || text.includes("_rsv_") || /^territory \d+$/.test(text);
}

function mergeEnemyIdentity(enemyNode, identity) {
  if (identity.sourceDataId) {
    enemyNode.id ??= [];
    addUniqueString(enemyNode.id, identity.sourceDataId);
  }

  if (identity.sourceBaseId) enemyNode.base_id ??= String(identity.sourceBaseId);
  if (identity.battleNpcNameId) enemyNode.bnpc_id ??= String(identity.battleNpcNameId);
  if (identity.modelId) enemyNode.model_id ??= String(identity.modelId);
  if (identity.level) enemyNode.level = Math.max(Number(enemyNode.level ?? 0), Number(identity.level));
}

function mergeHp(enemyNode, item) {
  const minHp = item.MinHp ?? item.minHp;
  const maxHp = item.MaxHp ?? item.maxHp;

  if (minHp) enemyNode.minHP = enemyNode.minHP ? Math.min(enemyNode.minHP, minHp) : minHp;
  if (maxHp) enemyNode.maxHP = enemyNode.maxHP ? Math.max(enemyNode.maxHP, maxHp) : maxHp;
}

function mergeSkillDetails(skillNode, item) {
  const damage = item.Damage ?? item.damage;
  const minDamage = damage?.Min ?? damage?.min;
  const maxDamage = damage?.Max ?? damage?.max;
  if (minDamage || maxDamage) {
    skillNode.damage ??= {};
    if (minDamage) skillNode.damage.min = skillNode.damage.min ? Math.min(skillNode.damage.min, minDamage) : minDamage;
    if (maxDamage) skillNode.damage.max = skillNode.damage.max ? Math.max(skillNode.damage.max, maxDamage) : maxDamage;
  }

  const statuses = item.StatusApplications ?? item.statusApplications ?? {};
  for (const status of Object.values(statuses)) {
    const statusId = status.StatusIdHex ?? status.statusIdHex ?? toHex(status.StatusId ?? status.statusId);
    if (!statusId) continue;
    skillNode.add_status ??= [];
    addUniqueString(skillNode.add_status, statusId);
    const mitigationType = status.MitigationType ?? status.mitigationType;
    const mitigationValue = getMitigationValue(status);
    if (mitigationValue) {
      skillNode.status_mitigation ??= {};
      skillNode.status_mitigation[statusId] = mitigationValue;
    }
  }
}

function mergeEnemyStatuses(enemyNode, item) {
  const statuses = item.StatusApplications ?? item.statusApplications ?? {};
  if (!Object.keys(statuses).length) return;
  enemyNode.status ??= {};

  for (const status of Object.values(statuses)) {
    const statusId = status.StatusIdHex ?? status.statusIdHex ?? toHex(status.StatusId ?? status.statusId);
    if (!statusId) continue;
    enemyNode.status[statusId] ??= {
      name: status.StatusName ?? status.statusName ?? "",
    };
  }
}

function mergeJobs(logdata, jobs) {
  logdata.Klassen_und_Jobs ??= {};
  for (const [jobName, job] of Object.entries(jobs)) {
    const name = job.Name ?? job.name ?? jobName;
    const node = (logdata.Klassen_und_Jobs[name] ??= {});
    node.id ??= String(job.ClassJobId ?? job.classJobId ?? "");
    node.abbreviation ??= job.Abbreviation ?? job.abbreviation ?? "";
    node.max_level_seen = Math.max(Number(node.max_level_seen ?? 0), Number(job.HighestSeenLevel ?? job.highestSeenLevel ?? 0));
    node.skill ??= {};
    node.status ??= {};

    for (const skill of Object.values(job.Skills ?? job.skills ?? {})) {
      const actionHex = skill.ActionIdHex ?? skill.actionIdHex ?? toHex(skill.ActionId ?? skill.actionId);
      if (!actionHex) continue;
      node.skill[actionHex] ??= { name: skill.Name ?? skill.name ?? "" };
      const damage = skill.Damage ?? skill.damage;
      if (damage?.Min || damage?.min || damage?.Max || damage?.max) {
        node.skill[actionHex].damage ??= {};
        const min = damage.Min ?? damage.min;
        const max = damage.Max ?? damage.max;
        if (min) node.skill[actionHex].damage.min = min;
        if (max) node.skill[actionHex].damage.max = max;
      }

      for (const status of Object.values(skill.StatusApplications ?? skill.statusApplications ?? {})) {
        const statusId = status.StatusIdHex ?? status.statusIdHex ?? toHex(status.StatusId ?? status.statusId);
        if (!statusId) continue;
        node.skill[actionHex].add_status ??= [];
        addUniqueString(node.skill[actionHex].add_status, statusId);
        const mitigationValue = getMitigationValue(status);
        if (mitigationValue) {
          node.skill[actionHex].status_mitigation ??= {};
          node.skill[actionHex].status_mitigation[statusId] = mitigationValue;
        }
      }
    }

    for (const status of Object.values(job.StatusApplications ?? job.statusApplications ?? {})) {
      const statusId = status.StatusIdHex ?? status.statusIdHex ?? toHex(status.StatusId ?? status.statusId);
      if (!statusId) continue;
      node.status[statusId] ??= { name: status.StatusName ?? status.statusName ?? "" };
    }
  }
}

function mergeMusic(logdata, music, hostedBgmNames) {
  logdata.Musik ??= {};
  for (const item of music) {
    const zone = item.TerritoryName ?? item.territoryName;
    const resolved = item.TerritoryNameResolved ?? item.territoryNameResolved ?? true;
    const bgmId = item.BgmId ?? item.bgmId;
    if (!zone || !resolved || !bgmId || isUnresolvedRsvName(zone)) continue;
    logdata.Musik[zone] ??= {};
    const node = (logdata.Musik[zone][toHex(bgmId)] ??= { id: String(bgmId) });
    const hostedName = hostedBgmNames.get(Number(bgmId));
    const name = item.Name ?? item.name ?? hostedName ?? "";
    const file = item.File ?? item.file ?? "";
    if (name) node.name = name;
    if (file) node.file = file;
    const count = item.Count ?? item.count;
    if (count) node.count = Number(node.count ?? 0) + Number(count);
  }
}

async function loadHostedBgmNames() {
  const names = new Map();
  try {
    if (typeof fetch !== "function") return names;
    const response = await fetch(hostedBgmCsvUrl);
    if (!response.ok) return names;
    parseHostedBgmCsv(await response.text(), names);
  } catch {
    return names;
  }

  return names;
}

function parseHostedBgmCsv(csv, names) {
  for (const line of csv.split(/\r?\n/)) {
    const parts = line.split(";");
    if (parts.length < 2) continue;
    const id = Number(parts[0]);
    const name = parts[1].trim();
    if (!id || !name || name.toLowerCase() === "n/a") continue;
    names.set(id, name);
  }
}

function mergeChatLines(logdata, chatLines) {
  logdata.ChatLines ??= [];
  const seen = new Set(logdata.ChatLines.map(line => `${line.zone}|${line.type_id}|${line.sender}|${line.message}|${line.seen_at_utc}`));

  for (const item of chatLines) {
    const zone = item.TerritoryName ?? item.territoryName;
    const resolved = item.TerritoryNameResolved ?? item.territoryNameResolved ?? true;
    if (!zone || !resolved || isUnresolvedRsvName(zone)) continue;

    const line = {
      zone,
      type_id: String(item.TypeId ?? item.typeId ?? ""),
      type: item.TypeName ?? item.typeName ?? "",
      category: item.Category ?? item.category ?? "",
      source_kind: item.SourceKind ?? item.sourceKind ?? "",
      target_kind: item.TargetKind ?? item.targetKind ?? "",
      sender: item.Sender ?? item.sender ?? "",
      message: item.Message ?? item.message ?? "",
      seen_at_utc: item.SeenAtUtc ?? item.seenAtUtc ?? "",
    };
    const key = `${line.zone}|${line.type_id}|${line.sender}|${line.message}|${line.seen_at_utc}`;
    if (seen.has(key)) continue;
    seen.add(key);
    logdata.ChatLines.push(line);
  }
}

function addUniqueString(array, value) {
  const text = String(value);
  if (!array.includes(text)) array.push(text);
}

function getMitigationValue(status) {
  const physical = status.PhysicalMitigationPercent ?? status.physicalMitigationPercent;
  const magical = status.MagicalMitigationPercent ?? status.magicalMitigationPercent;
  if (physical || magical) {
    const value = {};
    if (physical) value.physical = `${physical}%`;
    if (magical) value.magical = `${magical}%`;
    return value;
  }

  const mitigationType = status.MitigationType ?? status.mitigationType;
  return mitigationType && mitigationType !== "unknown" ? mitigationType : undefined;
}

function normalizeDamageType(value) {
  const text = String(value ?? "").toLowerCase();
  if (!text) return "";
  if (text.includes("dark") || text.includes("dunkel") || text.includes("unique") || text.includes("特")) return "Darkness";
  if (text.includes("magic") || text.includes("magisch") || text.includes("魔")) return "Magical";
  if (
    text.includes("physical") ||
    text.includes("physisch") ||
    text.includes("slashing") ||
    text.includes("piercing") ||
    text.includes("blunt") ||
    text.includes("shot") ||
    text.includes("斬") ||
    text.includes("突") ||
    text.includes("打") ||
    text.includes("射") ||
    text.includes("物理")
  ) {
    return "Physical";
  }

  return "";
}

function normalizeElement(value) {
  const text = String(value ?? "");
  return ["Fire", "Ice", "Wind", "Earth", "Lightning", "Water", "Unaspected"].includes(text) ? text : "";
}

function toHex(value) {
  if (!value) return "";
  return Number(value).toString(16).toUpperCase();
}
