# WebApp Folder and Test Restructuring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restructure `src/WebApp/src` from a single flat folder into clean domain layers (`components`, `services`, `store`, `types`, `utils`, `__tests__`) so test and source files are organized, uncluttered, and maintainable.

**Architecture:** Move existing files into dedicated subdirectories based on their layer. Group test files under `src/__tests__/` partitioned by module. Update all relative imports across source and test files. Fix the `Place` type import in `demo.ts` to satisfy `tsc` with `verbatimModuleSyntax`.

**Tech Stack:** React 19, TypeScript 5.8, Vite 6, Vitest 3, TailwindCSS 4.

**Spec:** User request to clean up messy test files in `src/WebApp/src` using Option 2 (layered structure).

## Global Constraints

- Must keep all 96 Vitest tests passing without modifying test assertions.
- `npm run typecheck` (`tsc --noEmit`) and `npm run build` (`tsc -b && vite build`) must pass without errors.
- Never run `git commit` or `git push` automatically (per `AGENTS.md`).
- Do not add or remove third-party dependencies.

## Review Focus

1. Import path breakage in components importing services, store, or utils.
2. Import path breakage in test files importing subject under test.
3. Type import resolution under `verbatimModuleSyntax: true` (e.g. `Place` in `demo.ts`).
4. CSS / Asset path resolution in `main.tsx` and `App.tsx`.
5. Vitest test runner discovering all test files in new locations.

---

### Task 1: Create Directories and Move Types & Utilities

**Files:**
- Create/Move:
  - `src/WebApp/src/types/place.ts` (from `src/WebApp/src/place.ts`)
  - `src/WebApp/src/types/tripTypes.ts` (from `src/WebApp/src/tripTypes.ts`)
  - `src/WebApp/src/utils/money.ts` (from `src/WebApp/src/money.ts`)
  - `src/WebApp/src/__tests__/utils/money.test.ts` (from `src/WebApp/src/money.test.ts`)

- [x] **Step 1: Move types and utils files**
- [x] **Step 2: Update imports inside `money.test.ts` to point to `../../utils/money`**
- [x] **Step 3: Run Vitest on `money.test.ts` to verify**
  Run: `cd src/WebApp; npx vitest run src/__tests__/utils/money.test.ts`
  Expected: PASS

---

### Task 2: Move and Update Services Layer

**Files:**
- Create/Move:
  - `src/WebApp/src/services/auth.ts`
  - `src/WebApp/src/services/api.ts`
  - `src/WebApp/src/services/driverLocation.ts`
  - `src/WebApp/src/services/locations.ts`
  - `src/WebApp/src/services/tripApi.ts`
  - `src/WebApp/src/__tests__/services/auth.test.ts`
  - `src/WebApp/src/__tests__/services/api.test.ts`
  - `src/WebApp/src/__tests__/services/driverLocation.test.ts`
  - `src/WebApp/src/__tests__/services/locations.test.ts`
  - `src/WebApp/src/__tests__/services/tripApi.test.ts`
  - `src/WebApp/src/__tests__/services/bookingController.test.ts`

- [x] **Step 1: Move service files and their tests into `src/services/` and `src/__tests__/services/`**
- [x] **Step 2: Update imports within services to point to `../types/*` and `./<peerService>`**
- [x] **Step 3: Update imports within tests to point to `../../services/*`, `../../types/*`, and `../../utils/*`**
- [x] **Step 4: Run Vitest on services tests**
  Run: `cd src/WebApp; npx vitest run src/__tests__/services`
  Expected: All 6 test files PASS

---

### Task 3: Move and Update Store Layer

**Files:**
- Create/Move:
  - `src/WebApp/src/store/demo.ts`
  - `src/WebApp/src/store/tripStore.ts`
  - `src/WebApp/src/store/useTrip.ts`
  - `src/WebApp/src/__tests__/store/demo.test.ts`
  - `src/WebApp/src/__tests__/store/tripStore.test.ts`

- [x] **Step 1: Move store files and their tests into `src/store/` and `src/__tests__/store/`**
- [x] **Step 2: Fix `import type { Place } from '../types/place'` and re-export in `demo.ts`**
- [x] **Step 3: Update imports in store files to point to `../types/*`, `../utils/*`, `../services/*`**
- [x] **Step 4: Update imports in store tests to point to `../../store/*`, `../../types/*`, etc.**
- [x] **Step 5: Run Vitest on store tests**
  Run: `cd src/WebApp; npx vitest run src/__tests__/store`
  Expected: All 2 test files PASS

---

### Task 4: Move and Update Components Layer and App Entry

**Files:**
- Create/Move:
  - `src/WebApp/src/components/AddressSearch.tsx`
  - `src/WebApp/src/components/DriverView.tsx`
  - `src/WebApp/src/components/RideMap.tsx`
  - `src/WebApp/src/components/RiderView.tsx`
  - `src/WebApp/src/__tests__/components/RideMap.test.tsx`
  - `src/WebApp/src/__tests__/components/views.test.tsx`
- Modify:
  - `src/WebApp/src/App.tsx`
  - `src/WebApp/src/main.tsx`

- [x] **Step 1: Move components and their tests into `src/components/` and `src/__tests__/components/`**
- [x] **Step 2: Update imports within components to point to sibling components, `../store/*`, `../services/*`, `../types/*`, `../utils/*`**
- [x] **Step 3: Update imports in `App.tsx` to point to `./components/*`, `./store/*`, `./services/*`, `./types/*`, `./utils/*`**
- [x] **Step 4: Update imports in component tests to point to `../../components/*`, `../../store/*`, etc.**
- [x] **Step 5: Remove old root files that were moved**
- [x] **Step 6: Run full test suite, typecheck, and build**
  Run:
  - `cd src/WebApp; npm test` (Expected: 11 passed, 96 tests)
  - `cd src/WebApp; npm run typecheck` (Expected: 0 errors)
  - `cd src/WebApp; npm run build` (Expected: Vite build success)
