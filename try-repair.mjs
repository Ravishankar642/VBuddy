import { createRequire as __cr } from "node:module"; const require = __cr(import.meta.url);

// src/db/flight-db.ts
import { existsSync, mkdirSync, renameSync } from "node:fs";
import { dirname } from "node:path";
async function loadSqlite() {
  const orig = process.emitWarning;
  process.emitWarning = ((w, ...rest) => {
    const msg = typeof w === "string" ? w : w?.message;
    if (typeof msg === "string" && msg.includes("SQLite")) return;
    return orig.call(process, w, ...rest);
  });
  try {
    return await import("node:sqlite");
  } finally {
    process.emitWarning = orig;
  }
}
var SCHEMA_VERSION = 1;
var SCHEMA = `
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);

-- One row per flight: a stretch of continuous telemetry from one aircraft.
CREATE TABLE IF NOT EXISTS flights (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  session_id  TEXT    NOT NULL,
  started_at  INTEGER NOT NULL,
  ended_at    INTEGER NOT NULL,
  source      TEXT    NOT NULL,
  aircraft    TEXT    NOT NULL DEFAULT '',
  first_seq   INTEGER NOT NULL,
  last_seq    INTEGER NOT NULL,
  frames      INTEGER NOT NULL DEFAULT 0,
  start_lat REAL, start_lon REAL, end_lat REAL, end_lon REAL,
  max_alt_ft  REAL    NOT NULL DEFAULT 0,
  max_gs_kt   REAL    NOT NULL DEFAULT 0,
  distance_nm REAL,
  plan_dep    TEXT,
  plan_arr    TEXT
);
CREATE INDEX IF NOT EXISTS flights_started ON flights(started_at);

-- The numbered telemetry stream. (session_id, seq) is exactly what a client resumes with.
CREATE TABLE IF NOT EXISTS frames (
  session_id TEXT    NOT NULL,
  seq        INTEGER NOT NULL,
  ts         INTEGER NOT NULL,
  data       TEXT    NOT NULL,
  PRIMARY KEY (session_id, seq)
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS frames_ts ON frames(ts);

CREATE TABLE IF NOT EXISTS landings (
  id        TEXT PRIMARY KEY,
  ts        INTEGER NOT NULL,
  flight_id INTEGER,
  json      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS landings_ts ON landings(ts);

-- Flights the pilot plans to fly.
CREATE TABLE IF NOT EXISTS planned_legs (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  dep        TEXT    NOT NULL,
  arr        TEXT    NOT NULL,
  aircraft   TEXT    NOT NULL DEFAULT '',
  planned_at INTEGER,
  notes      TEXT    NOT NULL DEFAULT '',
  created_at INTEGER NOT NULL
);
`;
var FlightDb = class _FlightDb {
  constructor(db2, file) {
    this.db = db2;
    this.file = file;
  }
  db;
  file;
  stmts = /* @__PURE__ */ new Map();
  /**
   * Opens (creating if needed) the database. A file that is damaged beyond use is
   * moved aside and replaced, so a bad file can never stop the bridge from
   * starting. Returns null only if SQLite itself isn't available.
   */
  static async open(file, log2) {
    let sqlite;
    try {
      sqlite = await loadSqlite();
    } catch (e) {
      log2.warn(`SQLite isn't available in this Node.js (${e.message}). Update to Node.js 22.13 or newer. The flight log is off.`);
      return null;
    }
    const attempt = () => {
      if (file !== ":memory:") mkdirSync(dirname(file), { recursive: true });
      const db2 = new sqlite.DatabaseSync(file);
      try {
        db2.exec("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 3000;");
        db2.exec(SCHEMA);
        const cols = db2.prepare("PRAGMA table_info(flights)").all();
        if (!cols.some((c) => c.name === "distance_nm")) db2.exec("ALTER TABLE flights ADD COLUMN distance_nm REAL");
        if (!cols.some((c) => c.name === "plan_dep")) db2.exec("ALTER TABLE flights ADD COLUMN plan_dep TEXT");
        if (!cols.some((c) => c.name === "plan_arr")) db2.exec("ALTER TABLE flights ADD COLUMN plan_arr TEXT");
        for (const [name, type] of [["plan_callsign", "TEXT"], ["plan_route", "TEXT"], ["plan_distance_nm", "REAL"], ["plan_ete_min", "REAL"], ["plan_ofp_id", "TEXT"], ["hidden", "INTEGER NOT NULL DEFAULT 0"]]) {
          if (!cols.some((c) => c.name === name)) db2.exec(`ALTER TABLE flights ADD COLUMN ${name} ${type}`);
        }
        db2.exec("CREATE TABLE IF NOT EXISTS landings_removed (id TEXT PRIMARY KEY, ts INTEGER NOT NULL, flight_id INTEGER, json TEXT NOT NULL, why TEXT)");
        const v = db2.prepare("PRAGMA user_version").get().user_version;
        if (v > SCHEMA_VERSION) throw new Error(`database is from a newer VBuddy (schema ${v})`);
        if (v < SCHEMA_VERSION) db2.exec(`PRAGMA user_version = ${SCHEMA_VERSION}`);
        db2.prepare("SELECT COUNT(*) FROM frames").get();
      } catch (e) {
        db2.close();
        throw e;
      }
      return db2;
    };
    try {
      return new _FlightDb(attempt(), file);
    } catch (e) {
      if (file === ":memory:" || !existsSync(file)) {
        log2.warn(`Couldn't open the flight log ${file}: ${e.message}. The flight log is off.`);
        return null;
      }
      const aside = `${file}.damaged-${Date.now()}`;
      try {
        renameSync(file, aside);
        for (const ext of ["-wal", "-shm"]) if (existsSync(file + ext)) renameSync(file + ext, aside + ext);
        log2.warn(`The flight log ${file} couldn't be read (${e.message}). Moved it to ${aside} and started a new one.`);
        return new _FlightDb(attempt(), file);
      } catch (e2) {
        log2.warn(`Couldn't open the flight log: ${e2.message}. The flight log is off.`);
        return null;
      }
    }
  }
  stmt(sql) {
    let s = this.stmts.get(sql);
    if (!s) {
      s = this.db.prepare(sql);
      this.stmts.set(sql, s);
    }
    return s;
  }
  tx(fn) {
    this.db.exec("BEGIN");
    try {
      const r = fn();
      this.db.exec("COMMIT");
      return r;
    } catch (e) {
      try {
        this.db.exec("ROLLBACK");
      } catch {
      }
      throw e;
    }
  }
  /* ------------------------------------------------------------ meta */
  getMeta(key) {
    const r = this.stmt("SELECT value FROM meta WHERE key = ?").get(key);
    return r?.value ?? null;
  }
  setMeta(key, value) {
    this.stmt("INSERT INTO meta(key, value) VALUES(?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value").run(key, value);
  }
  /* ------------------------------------------------------------ frames */
  insertFrames(sessionId, frames) {
    if (!frames.length) return;
    const ins = this.stmt("INSERT OR REPLACE INTO frames(session_id, seq, ts, data) VALUES(?, ?, ?, ?)");
    this.tx(() => {
      for (const f of frames) ins.run(sessionId, f.seq, f.ts, JSON.stringify(f.data));
    });
  }
  /** Frames with fromSeq <= seq <= toSeq, keeping every `stride`th one counted from fromSeq. Oldest first. */
  frames(sessionId, fromSeq, toSeq, stride = 1) {
    const rows = this.stmt(
      "SELECT seq, ts, data FROM frames WHERE session_id = ? AND seq BETWEEN ? AND ? AND ((seq - ?) % ?) = 0 ORDER BY seq"
    ).all(sessionId, fromSeq, toSeq, fromSeq, Math.max(1, stride));
    const out = [];
    for (const r of rows) {
      try {
        out.push({ seq: r.seq, ts: r.ts, data: JSON.parse(r.data) });
      } catch {
      }
    }
    return out;
  }
  /** Deletes telemetry older than the cutoff. Flights and landings are kept. Returns rows removed. */
  pruneFrames(olderThanTs) {
    return Number(this.stmt("DELETE FROM frames WHERE ts < ?").run(olderThanTs).changes);
  }
  frameCount() {
    return Number(this.stmt("SELECT COUNT(*) AS n FROM frames").get().n);
  }
  /* ------------------------------------------------------------ flights */
  openFlight(f) {
    const r = this.stmt(
      `INSERT INTO flights(session_id, started_at, ended_at, source, aircraft, first_seq, last_seq, frames, start_lat, start_lon, end_lat, end_lon)
       VALUES(?, ?, ?, ?, ?, ?, ?, 0, ?, ?, ?, ?)`
    ).run(f.sessionId, f.startedAt, f.startedAt, f.source, f.aircraft, f.firstSeq, f.firstSeq, f.lat, f.lon, f.lat, f.lon);
    return Number(r.lastInsertRowid);
  }
  updateFlight(id, u) {
    this.stmt(
      `UPDATE flights SET ended_at = ?, aircraft = COALESCE(?, aircraft), last_seq = ?, frames = ?, end_lat = ?, end_lon = ?,
         max_alt_ft = MAX(max_alt_ft, ?), max_gs_kt = MAX(max_gs_kt, ?) WHERE id = ?`
    ).run(u.endedAt, u.aircraft ?? null, u.lastSeq, u.frames, u.endLat, u.endLon, u.maxAltFt, u.maxGsKt, id);
  }
  mapFlight(r) {
    return {
      id: Number(r.id),
      sessionId: String(r.session_id),
      startedAt: Number(r.started_at),
      endedAt: Number(r.ended_at),
      source: String(r.source),
      aircraft: String(r.aircraft),
      firstSeq: Number(r.first_seq),
      lastSeq: Number(r.last_seq),
      frames: Number(r.frames),
      startLat: r.start_lat === null ? null : Number(r.start_lat),
      startLon: r.start_lon === null ? null : Number(r.start_lon),
      endLat: r.end_lat === null ? null : Number(r.end_lat),
      endLon: r.end_lon === null ? null : Number(r.end_lon),
      maxAltFt: Number(r.max_alt_ft),
      maxGsKt: Number(r.max_gs_kt),
      landings: Number(r.landings ?? 0),
      distanceNm: r.distance_nm === null || r.distance_nm === void 0 ? null : Number(r.distance_nm),
      planDep: r.plan_dep ? String(r.plan_dep) : null,
      planArr: r.plan_arr ? String(r.plan_arr) : null,
      planCallsign: r.plan_callsign ? String(r.plan_callsign) : null,
      planRoute: r.plan_route ? String(r.plan_route) : null,
      planDistanceNm: r.plan_distance_nm === null || r.plan_distance_nm === void 0 ? null : Number(r.plan_distance_nm),
      planEteMin: r.plan_ete_min === null || r.plan_ete_min === void 0 ? null : Number(r.plan_ete_min),
      planOfpId: r.plan_ofp_id ? String(r.plan_ofp_id) : null
    };
  }
  /** Remembers the plan this flight was flown against. Only the first plan is kept, unless `replace`. */
  setFlightPlan(id, dep, arr, more = null, replace = false) {
    this.stmt(
      `UPDATE flights SET plan_dep = ?, plan_arr = ?, plan_callsign = ?, plan_route = ?, plan_distance_nm = ?, plan_ete_min = ?, plan_ofp_id = ?
       WHERE id = ?${replace ? "" : " AND plan_dep IS NULL"}`
    ).run(dep, arr, more?.callsign ?? null, more?.route ?? null, more?.distanceNm ?? null, more?.eteMin ?? null, more?.ofpId ?? null, id);
  }
  /** Takes a flight out of the lists (a stretch in the menu, a spawn that never went anywhere). It stays in the file. */
  hideFlight(id) {
    this.stmt("UPDATE flights SET hidden = 1 WHERE id = ?").run(id);
  }
  /** Every flight, oldest first, with what the repair needs. Includes hidden ones. */
  allFlights() {
    const rows = this.stmt("SELECT f.*, (SELECT COUNT(*) FROM landings l WHERE l.flight_id = f.id) AS landings FROM flights f ORDER BY f.started_at, f.id").all();
    return rows.map((r) => ({ ...this.mapFlight(r), hidden: Number(r.hidden ?? 0) === 1 }));
  }
  setFlightEnds(id, p) {
    const f = this.flight(id);
    if (!f) return;
    this.stmt("UPDATE flights SET start_lat = ?, start_lon = ?, end_lat = ?, end_lon = ?, started_at = ? WHERE id = ?").run(
      p.startLat === void 0 ? f.startLat : p.startLat,
      p.startLon === void 0 ? f.startLon : p.startLon,
      p.endLat === void 0 ? f.endLat : p.endLat,
      p.endLon === void 0 ? f.endLon : p.endLon,
      p.startedAt ?? f.startedAt,
      id
    );
  }
  /** [ts, lat, lon, onGround] of a flight's frames, oldest first, at most `limit`. */
  flightPositions(id, limit = 5e3) {
    const f = this.flight(id);
    if (!f) return [];
    const rows = this.stmt(
      "SELECT ts, json_extract(data,'$.lat') AS lat, json_extract(data,'$.lon') AS lon, json_extract(data,'$.onGround') AS g FROM frames WHERE session_id = ? AND seq BETWEEN ? AND ? ORDER BY seq LIMIT ?"
    ).all(f.sessionId, f.firstSeq, f.lastSeq, limit);
    return rows.map((r) => [Number(r.ts), Number(r.lat), Number(r.lon), !!r.g && r.g !== 0]);
  }
  /** The landings of one flight: id, time, position. */
  flightLandings(id) {
    const rows = this.stmt("SELECT id, ts, json_extract(json,'$.lat') AS lat, json_extract(json,'$.lon') AS lon FROM landings WHERE flight_id = ? ORDER BY ts").all(id);
    return rows.map((r) => ({ id: String(r.id), ts: Number(r.ts), lat: Number(r.lat), lon: Number(r.lon) }));
  }
  /** Moves landings into landings_removed (kept, but no longer listed). */
  removeLandings(ids, why) {
    for (const id of ids) {
      this.stmt("INSERT OR IGNORE INTO landings_removed(id, ts, flight_id, json, why) SELECT id, ts, flight_id, json, ? FROM landings WHERE id = ?").run(why, id);
      this.stmt("DELETE FROM landings WHERE id = ?").run(id);
    }
  }
  setFlightDistance(id, nm2) {
    this.stmt("UPDATE flights SET distance_nm = ? WHERE id = ?").run(nm2, id);
  }
  /** Newest first. */
  flights(limit = 50) {
    const rows = this.stmt(
      "SELECT f.*, (SELECT COUNT(*) FROM landings l WHERE l.flight_id = f.id) AS landings FROM flights f WHERE COALESCE(f.hidden, 0) = 0 ORDER BY f.started_at DESC, f.id DESC LIMIT ?"
    ).all(limit);
    return rows.map((r) => this.mapFlight(r));
  }
  flight(id) {
    const r = this.stmt("SELECT f.*, (SELECT COUNT(*) FROM landings l WHERE l.flight_id = f.id) AS landings FROM flights f WHERE f.id = ?").get(id);
    return r ? this.mapFlight(r) : null;
  }
  /** The flight's path, thinned to at most ~maxPoints. Null if there's no such flight. */
  track(id, maxPoints = 2e3) {
    const f = this.flight(id);
    if (!f) return null;
    const n = f.lastSeq - f.firstSeq + 1;
    const stride = Math.max(1, Math.ceil(n / Math.max(2, maxPoints)));
    const rows = this.stmt(
      `SELECT ts, json_extract(data,'$.lat') AS lat, json_extract(data,'$.lon') AS lon, json_extract(data,'$.altMsl') AS alt,
              json_extract(data,'$.gs') AS gs, json_extract(data,'$.hdgTrue') AS hdg
       FROM frames WHERE session_id = ? AND seq BETWEEN ? AND ? AND ((seq - ?) % ?) = 0 ORDER BY seq`
    ).all(f.sessionId, f.firstSeq, f.lastSeq, f.firstSeq, stride);
    return rows.map((r) => [Number(r.ts), Number(r.lat), Number(r.lon), Math.round(Number(r.alt)), Math.round(Number(r.gs)), Math.round(Number(r.hdg))]);
  }
  /* ------------------------------------------------------------ landings */
  /** Returns false if the landing was already stored. */
  insertLanding(l, flightId) {
    const r = this.stmt("INSERT OR IGNORE INTO landings(id, ts, flight_id, json) VALUES(?, ?, ?, ?)").run(l.id, l.ts, flightId, JSON.stringify(l));
    return Number(r.changes) > 0;
  }
  /** Raw landing JSON, oldest first, for the newest `limit` landings. The caller validates. */
  recentLandingJson(limit) {
    const rows = this.stmt("SELECT json FROM (SELECT ts, json FROM landings ORDER BY ts DESC LIMIT ?) ORDER BY ts").all(limit);
    return rows.map((r) => r.json);
  }
  landingCount() {
    return Number(this.stmt("SELECT COUNT(*) AS n FROM landings").get().n);
  }
  /* ------------------------------------------------------------ planned legs */
  mapLeg(r) {
    return {
      id: Number(r.id),
      dep: String(r.dep),
      arr: String(r.arr),
      aircraft: String(r.aircraft),
      plannedAt: r.planned_at === null ? null : Number(r.planned_at),
      notes: String(r.notes),
      createdAt: Number(r.created_at)
    };
  }
  /** Soonest planned first; legs with no date come last, oldest entry first. */
  plannedLegs() {
    const rows = this.stmt("SELECT * FROM planned_legs ORDER BY planned_at IS NULL, planned_at, id").all();
    return rows.map((r) => this.mapLeg(r));
  }
  /** Applies an add / update / delete. Returns false when an update or delete named a leg that isn't there. */
  applyLeg(c, now = Date.now()) {
    if (c.op === "add") {
      this.stmt("INSERT INTO planned_legs(dep, arr, aircraft, planned_at, notes, created_at) VALUES(?, ?, ?, ?, ?, ?)").run(c.dep, c.arr, c.aircraft, c.plannedAt, c.notes, now);
      return true;
    }
    if (c.op === "update") {
      return Number(this.stmt("UPDATE planned_legs SET dep = ?, arr = ?, aircraft = ?, planned_at = ?, notes = ? WHERE id = ?").run(c.dep, c.arr, c.aircraft, c.plannedAt, c.notes, c.id).changes) > 0;
    }
    return Number(this.stmt("DELETE FROM planned_legs WHERE id = ?").run(c.id).changes) > 0;
  }
  /* ------------------------------------------------------------ lifecycle */
  close() {
    this.stmts.clear();
    try {
      this.db.exec("PRAGMA wal_checkpoint(TRUNCATE)");
    } catch {
    }
    this.db.close();
  }
};

// src/sim/flight-gate.ts
var R_NM = 3440.065;
var rad = Math.PI / 180;
function nm(aLat, aLon, bLat, bLon) {
  const a = Math.sin((bLat - aLat) * rad / 2) ** 2 + Math.cos(aLat * rad) * Math.cos(bLat * rad) * Math.sin((bLon - aLon) * rad / 2) ** 2;
  return 2 * R_NM * Math.asin(Math.min(1, Math.sqrt(a)));
}

// src/db/repair.ts
function repairPlans(db2, log2, force = false) {
  const KEY2 = "repair-plans-1";
  if (!force && db2.getMeta(KEY2)) return 0;
  const flights = db2.allFlights().sort((a, b) => a.startedAt - b.startedAt);
  let moved = 0;
  for (let i = 0; i < flights.length; i++) {
    const h = flights[i];
    if (!h.hidden || !h.planDep || !h.planArr) continue;
    for (let j = i + 1; j < flights.length; j++) {
      const f = flights[j];
      if (f.startedAt - h.endedAt > 36e5) break;
      if (f.hidden) continue;
      if (f.planDep) break;
      const near = h.startLat === null || h.startLon === null || f.startLat === null || f.startLon === null || nm(h.startLat, h.startLon, f.startLat, f.startLon) < 8;
      if (!near) break;
      db2.setFlightPlan(f.id, h.planDep, h.planArr, { callsign: h.planCallsign ?? null, route: h.planRoute ?? null, distanceNm: h.planDistanceNm ?? null, eteMin: h.planEteMin ?? null, ofpId: h.planOfpId ?? null });
      f.planDep = h.planDep;
      moved++;
      break;
    }
  }
  db2.setMeta(KEY2, String(Date.now()));
  if (moved) log2?.info(`Gave ${moved} flight(s) back the SimBrief plan a short stretch before them had taken.`);
  return moved;
}

// ../../../../../../tmp/claude-0/-home-claude/e6d2f548-f804-55fd-86d7-c33bab7edec3/scratchpad/try-repair.ts
var log = { info: (m) => console.log(m), warn: (m) => console.log("WARN", m), error: (m) => console.log("ERR", m), debug: () => {
} };
var db = await FlightDb.open(process.argv[2], log);
if (!db) throw new Error("no db");
console.log("moved", repairPlans(db, log, true));
for (const f of db.allFlights().slice(-25).reverse()) console.log(f.id, f.hidden ? "H" : " ", (f.aircraft ?? "").slice(0, 22), f.planDep, f.planArr, f.planCallsign);
db.close();
