// Golden traces for the C# port of the ShramkoGSFPV flight model (Assets/Scripts/Sim/Fpv).
//
//   SHRAMKO_DIR=/path/to/ShramkoGSFPV-main npx tsx gen_golden.mts
//
// Runs the ORIGINAL TypeScript model (packages/sim-core, MIT, Andrii Shramko) over scripted channel
// inputs and synthetic contact worlds, and writes golden/*.json: the scenario (so the C# side replays the
// very same inputs) plus the full state vector every `every` ticks and the events. tools/fpv (dotnet run)
// replays them on the C# port and fails when any sampled value drifts.
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

const root = process.env.SHRAMKO_DIR;
if (!root) throw new Error('set SHRAMKO_DIR to the ShramkoGSFPV checkout');
const core = await import(join(root, 'packages/sim-core/src/index.ts'));
const { Sim, compileParams, MODE_CHANNEL } = core;

type World = null | { kind: 'floor'; y: number } | { kind: 'wall'; x: number } | { kind: 'corner'; y: number; x: number };
interface Seg { ticks: number; ch: number[] }
interface Scenario {
    name: string; preset: string; overrides?: Record<string, unknown>; world: World;
    start: [number, number, number, number]; segs: Seg[]; every: number;
}

function halfSpace(nx: number, ny: number, nz: number, d: number) {
    return {
        sweep(c0: Float64Array, c1: Float64Array, r: Float64Array, pad: Float64Array, n: number, out: any): number {
            let best = -1;
            for (let i = 0; i < n; i++) {
                const R = r[i] + pad[i];
                const a = nx * c0[i * 3] + ny * c0[i * 3 + 1] + nz * c0[i * 3 + 2] - d - R;
                const b = nx * c1[i * 3] + ny * c1[i * 3 + 1] + nz * c1[i * 3 + 2] - d - R;
                let t = -1;
                if (a <= 0) t = 0; else if (b < 0) t = a / (a - b);
                if (t >= 0 && (best < 0 || t < best)) { best = t; out.sphere = i; out.nx = nx; out.ny = ny; out.nz = nz; }
            }
            return best;
        },
        pushOut(x: number, y: number, z: number, r: number, o: any): boolean {
            const dist = nx * x + ny * y + nz * z - d - r;
            if (dist >= 0) return false;
            o.x = -nx * dist; o.y = -ny * dist; o.z = -nz * dist;
            return true;
        }
    };
}
function union(a: any, b: any) {
    const tmp = { sphere: -1, nx: 0, ny: 0, nz: 0 };
    return {
        sweep(c0: any, c1: any, r: any, pad: any, n: number, out: any): number {
            const ta = a.sweep(c0, c1, r, pad, n, out);
            const na = { ...out };
            const tb = b.sweep(c0, c1, r, pad, n, tmp);
            if (tb >= 0 && (ta < 0 || tb < ta)) { Object.assign(out, tmp); return tb; }
            if (ta >= 0) Object.assign(out, na);
            return ta;
        },
        pushOut(x: number, y: number, z: number, r: number, o: any): boolean {
            if (a.pushOut(x, y, z, r, o)) return true;
            return b.pushOut(x, y, z, r, o);
        }
    };
}
function makeWorld(w: World): any {
    if (!w) return null;
    if (w.kind === 'floor') return halfSpace(0, 1, 0, w.y);
    if (w.kind === 'wall') return halfSpace(-1, 0, 0, -w.x);               // solid for x > w.x
    return union(halfSpace(0, 1, 0, w.y), halfSpace(-1, 0, 0, -w.x));
}

const M = MODE_CHANNEL; // acro -1, horizon 0, angle 1
const ch = (roll: number, pitch: number, thr01: number, yaw: number, arm: boolean, mode: number): number[] => [roll, pitch, thr01 * 2 - 1, yaw, arm ? 1 : -1, mode, 0, 0];
const arm = (mode: number): Seg[] => [{ ticks: 20, ch: ch(0, 0, 0, 0, false, mode) }, { ticks: 80, ch: ch(0, 0, 0, 0, true, mode) }];

const scenarios: Scenario[] = [
    { // acro flying: climb, hover-ish, roll flip, pitch flip, yaw spin, chop and recover
        name: 'acro_pavo', preset: 'pavo20pro-3s', world: null, start: [0, 50, 0, 0], every: 25,
        segs: [...arm(M.acro),
            { ticks: 600, ch: ch(0, 0, 0.62, 0, true, M.acro) },
            { ticks: 350, ch: ch(1, 0, 0.62, 0, true, M.acro) },
            { ticks: 300, ch: ch(0, 0, 0.55, 0, true, M.acro) },
            { ticks: 300, ch: ch(0, 1, 0.7, 0.3, true, M.acro) },
            { ticks: 500, ch: ch(-0.4, 0.3, 0.6, -0.8, true, M.acro) },
            { ticks: 400, ch: ch(0, 0, 0.0, 0, true, M.acro) },
            { ticks: 700, ch: ch(0.1, 0, 0.8, 0, true, M.acro) }]
    },
    { // angle mode with stick moves and a throttle chop (descent into the own wake on a wake preset)
        name: 'angle_nazgul_wash', preset: 'nazgulf5v3-6s', world: null, start: [3, 80, -2, 35], every: 25,
        segs: [...arm(M.angle),
            { ticks: 700, ch: ch(0, 0, 0.6, 0, true, M.angle) },
            { ticks: 800, ch: ch(0, 0.6, 0.62, 0.2, true, M.angle) },
            { ticks: 500, ch: ch(0.5, 0, 0.6, 0, true, M.angle) },
            { ticks: 700, ch: ch(0, 0, 0.1, 0, true, M.angle) },
            { ticks: 900, ch: ch(0, 0, 0.7, 0, true, M.angle) },
            { ticks: 600, ch: ch(0, -0.5, 0.6, -0.4, true, M.angle) }]
    },
    { // horizon + the mode switch changing mid-flight (acro -> horizon -> angle -> acro)
        name: 'modes_pavo2_4s', preset: 'pavo20pro2-4s', world: null, start: [0, 30, 0, -90], every: 25,
        segs: [...arm(M.acro),
            { ticks: 500, ch: ch(0, 0, 0.6, 0, true, M.acro) },
            { ticks: 400, ch: ch(0.8, 0.2, 0.6, 0, true, M.acro) },
            { ticks: 600, ch: ch(0.3, 0.4, 0.6, 0, true, M.horizon) },
            { ticks: 600, ch: ch(0, 0.2, 0.6, 0.3, true, M.angle) },
            { ticks: 500, ch: ch(-0.9, 0, 0.6, 0, true, M.acro) }]
    },
    { // free fall onto a floor: crash
        name: 'floor_crash', preset: 'pavo20pro-3s', world: { kind: 'floor', y: 0 }, start: [0, 3, 0, 0], every: 10,
        segs: [...arm(M.acro), { ticks: 1500, ch: ch(0, 0, 0.0, 0, true, M.acro) }, { ticks: 2500, ch: ch(0, 0, 0.0, 0, false, M.acro) }]
    },
    { // gentle descent onto a floor (below the crash speed): bounce / slide contact, then rest on it and take off again
        name: 'floor_soft', preset: 'pavo20pro-3s', world: { kind: 'floor', y: 0 }, start: [0, 0.5, 0, 20], every: 10,
        segs: [...arm(M.angle),
            { ticks: 500, ch: ch(0, 0, 0.3, 0, true, M.angle) },
            { ticks: 400, ch: ch(0.2, 0.3, 0.2, 0, true, M.angle) },
            { ticks: 700, ch: ch(0, 0, 0.75, 0, true, M.angle) }]
    },
    { // into a wall at moderate speed (angle mode, roll right): a crash or a bump, and the pinned pass in a corner
        name: 'corner_pinned', preset: 'pavo20pro-3s', world: { kind: 'corner', y: 0, x: 2.0 }, start: [0, 0.3, 0, 0], every: 10,
        segs: [...arm(M.angle),
            { ticks: 600, ch: ch(0, 0, 0.65, 0, true, M.angle) },
            { ticks: 1500, ch: ch(0.5, 0.0, 0.55, 0, true, M.angle) },
            { ticks: 800, ch: ch(0.8, 0.3, 0.45, 0, true, M.angle) }]
    },
    { // heavy preset with its own level feed-forward and wake torque, flown into a floor on its side
        name: 'taurus_floor', preset: 'taurusx8-komodo-6s', world: { kind: 'floor', y: 0 }, start: [0, 6, 0, 0], every: 25,
        segs: [...arm(M.angle),
            { ticks: 900, ch: ch(0, 0, 0.55, 0, true, M.angle) },
            { ticks: 700, ch: ch(0.7, 0.4, 0.5, 0.3, true, M.angle) },
            { ticks: 1500, ch: ch(0, 0, 0.2, 0, true, M.angle) }]
    },
    { // tiny 1S whoop with the gravity override (moon-ish) and a rate override
        name: 'air65_lowg', preset: 'air65-1s', world: { kind: 'floor', y: 0 }, start: [0, 1, 0, 0], every: 25,
        overrides: { gravity: 1.62, gravityMode: 'honest' },
        segs: [...arm(M.acro), { ticks: 800, ch: ch(0, 0, 0.55, 0, true, M.acro) }, { ticks: 600, ch: ch(0.5, 0.5, 0.45, 0.5, true, M.acro) }, { ticks: 800, ch: ch(0, 0, 0.1, 0, true, M.acro) }]
    }
];

mkdirSync(join(import.meta.dirname ?? '.', 'golden'), { recursive: true });
for (const sc of scenarios) {
    const preset = JSON.parse(readFileSync(join(root, 'packages/sim-core/presets', `${sc.preset}.json`), 'utf8'));
    const p = compileParams(preset, sc.overrides ?? {});
    const sim = new Sim(p, makeWorld(sc.world));
    sim.reset(...sc.start);
    const samples: { tick: number; s: number[] }[] = [];
    const events: any[] = [];
    let evIdx = 0, tick = 0;
    for (const seg of sc.segs) {
        for (let k = 0; k < seg.ticks; k++) {
            sim.setChannels(seg.ch);
            sim.step();
            tick++;
            if (tick % sc.every === 0) samples.push({ tick, s: Array.from(sim.s.subarray(0, 65)) });
            for (; evIdx < sim.events.length; evIdx++) { const e = sim.events[evIdx]; events.push({ ...e }); }
        }
    }
    const out = {
        scenario: { name: sc.name, preset: sc.preset, overrides: sc.overrides ?? {}, world: sc.world, start: sc.start, segs: sc.segs, every: sc.every },
        compiled: { mass: p.mass, vNom: p.vNom, hoverThr: sim.hoverThr, tmaxMotorNom: p.tmaxMotorNom, twr: p.twr, bodyBottom: p.bodyBottom, wakeLoss: p.wakeLoss, wakeShake: p.wakeShake, wakeArea: p.wakeArea, ffGain: p.level.ffGain, ffSmoothMs: p.level.ffSmoothMs },
        samples, events
    };
    writeFileSync(join(import.meta.dirname ?? '.', 'golden', `${sc.name}.json`), JSON.stringify(out));
    const crashes = events.filter((e) => e.type === 'crash').length, contacts = events.filter((e) => e.type === 'contact').length;
    console.log(`${sc.name.padEnd(20)} ${tick} ticks  samples ${samples.length}  events ${events.length} (crash ${crashes}, contact ${contacts})  final y ${sim.s[1].toFixed(3)}`);
}
