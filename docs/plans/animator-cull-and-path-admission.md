# Plan: Animator CullCompletely emergency + path admission

**Status:** built; light-load live gate PASS; stress measure open  
**Date:** 2026-07-28  
**Targets:** EfficientServer (Harmony, net48)

## A. Animator emergency exit (CullCompletely)

### Problem
`Animator.enabled = false` stops headless eval (~40% of saturated 64p frame) but
restore leaves `deltaPosition = 0` forever. Governor tier 2 stays default-off.

### Design
1. Never toggle `enabled` for emergency / `es animoff`.
2. On enter: save each live enemy animator's `cullingMode`, set
   `AnimatorCullingMode.CullCompletely`, keep `enabled = true`.
3. On exit: restore saved modes by entityId sweep (no Rebind).
4. While active: periodic sweep (existing governor) + `es animoff` calls Enter.
5. `AnimatorLodPatch`: if emergency Active, skip managed Update/LateUpdate and do
   not re-enable animators (avoids fighting emergency).

### Fidelity / measure (post-build)
- `es animstate`: after animon, `dp > 0` for moving zombies (not 0.0000 crawl).
- Optional A/B: animoff under heavy load should cut frame like prior 147->85 class.
- Human cycle still required before `Governor.AnimatorEmergency` default true.

### Config
No new knobs. `Governor.AnimatorEmergency` stays default **false**.

## B. Path admission (A2)

### Problem
`EntityAlive.FindPath` always enqueues (Y-clamp only). Drain is bounded; enqueue is not.
BM path spam is a spike risk, not the steady-state tick wall.

### Design
Harmony **prefix** on `EntityAlive.FindPath(Vector3, float, bool, EAIBase)`:

1. Never drop if attack target / investigate / alert ticks / active sleeper.
2. Optional distance drop: `aiClosestPlayerDistSq >= DropPathWhenFarDistSq` (0 = off).
3. Optional per-frame enqueue cap: `MaxPathEnqueuesPerTick` (0 = unlimited).
4. Defaults both **0** (vanilla). Honest names only.

### Config
```text
Pathfinding.MaxPathEnqueuesPerTick = 0   // 0 = unlimited
Pathfinding.DropPathWhenFarDistSq = 0    // 0 = no distance drop
```

### Measure
Synthetic BM / path-spam load: path queue pressure, TickEntities, no stuck ferals
near players. Do not claim ms/tick win without APM session IDs.

## C. Delivery checklist
- [x] Implement AnimatorEmergency rewrite
- [x] Wire console animoff/animon/animstate
- [x] AnimatorLod coexistence
- [x] PathAdmissionPatch + Config + Normalize + tests
- [x] ModApi register + ConfigNote
- [x] CONFIG.md / FEATURES / PERF brief / TODO
- [x] `make build` + `make test` (build OK; tests PASS with DOTNET_ROOT)
- [x] Commit (optimizer only for code) `ca9a0fd`

## D. Light-load live gate (2026-08-02)

- [x] Install EfficientServer with CullCompletely + PathAdmission
- [x] Loadgen 8 bots + ~100 endgame (Navezgane)
- [x] animoff/animon + animstate dp check
- [x] path cap/drop reload fidelity
- [ ] Stress (64p, over-budget frame) for animator ms win
