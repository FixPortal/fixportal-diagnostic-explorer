# Diagnostics UI Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish the PrimeNG re-platform of `diagnostics-web` to the locked design — Layout A, "muted slate / punchy severity" palette, resizable panes/columns, severity-dot category nav, a tabbed detail panel with a restored Trace Scope tree, and a polished Retro view — then remove Material and pass all quality gates.

**Architecture:** Single fixed full-viewport shell. Two modes (Realtime, Retro) share one shell, switched by a segmented toggle. PrimeNG components (`p-splitter`, `p-table`, `p-panel`/`p-fieldset`, `p-tabs`, `p-select`, `p-datepicker`, `p-checkbox`) over the Aura dark theme, with a custom CSS-custom-property palette. Data layer (models, `diag-hub` SignalR to local `/web-hub`, Retro Mongo search) is **unchanged**. Builds on the existing `primeng-migration` branch.

**Tech Stack:** Angular 21, PrimeNG 21 (Aura), Tailwind 3, `@angular/cdk` (Clipboard only), Jest, ESLint + SonarJS.

**Spec:** `docs/superpowers/specs/2026-06-03-diagnostics-ui-redesign-design.md` (source of truth for palette tokens, layout, behaviour). Visual mockups: `.superpowers/brainstorm/587253-1780497863/content/*.html` (ephemeral reference).

**Testing approach:** TDD for logic-bearing work (Trace Scope tree renderer; any model-touching change). For pure styling/layout tasks, verification = `npm run build` (prod, clean) + `npm run lint` (clean) + a visual check (`npm start` → localhost:4200, or the visual companion), and all existing Jest tests stay green. Don't add unit tests that only assert CSS.

**Per-task git:** the branch is `primeng-migration`. Commit at the end of each task. Run from the repo's `diagnostics-web` directory.

---

## File structure (what changes, and why)

| File | Responsibility | Action |
|---|---|---|
| `src/styles.scss` | global palette tokens, focus ring, scrollbars, severity classes | Modify (add `:root.app-dark` token block; remove leftover Material/ad-hoc colour) |
| `src/app/app.component.{html,scss,ts}` | shell: top bar, mode toggle, sidebar⇆main splitter | Modify (as-split → `p-splitter`; toggle → `p-selectButton`) |
| `src/app/app.module.ts` | NgModule imports/providers | Modify (add Splitter/SelectButton/Select/DatePicker/ProgressBar/Tree modules; later drop Material) |
| `src/app/realtime-nav/*` | process list | Modify (`p-table` `resizableColumns`, palette) |
| `src/app/realtime-display/{html,scss,ts}` | category nav + content + detail splitter | Modify (tabs-right → severity-dot list; flat detail → content/detail `p-splitter` + tabbed detail) |
| `src/app/category-nav/*` | **new** severity-dot category list | Create |
| `src/app/realtime-category/*` | property/event panels | Modify (palette alignment) |
| `src/app/realtime-events/{html,scss}` | event sink table | Modify (severity-row treatment) |
| `src/app/event-detail/*` | **new** tabbed Detail/Trace-Scope panel (shared by Realtime + Retro) | Create |
| `src/app/trace-scope/*` | **new** Trace Scope tree renderer (replaces `collapsible-region`) | Create |
| `src/app/collapsible-region/*` | old broken renderer | Delete (after trace-scope lands) |
| `src/app/retro-nav/*` | search form | Modify (PrimeNG controls, focus ring) |
| `src/app/retro-display/{html,scss,ts}` | results table + detail | Modify (`p-table` resizable, severity rows, shared event-detail) |
| `src/app/services/layout-size.service.ts` | **new** persist splitter sizes (localStorage) | Create |
| `package.json` / `angular.json` | deps + budgets | Modify (remove `@angular/material`; restore initial bundle budget 4mb→2mb) |

---

## Task 1: Global palette tokens + focus ring

**Files:**
- Modify: `src/styles.scss`

- [ ] **Step 1: Replace the Material theme block + ad-hoc colours with the palette token block.** In `src/styles.scss`, remove the `@use '@angular/material'`/`mat.*` includes and the scattered legacy `.mat-*` colour rules (the Tailwind directives + app utility classes like `event-level-*`, scrollbars, `.noselect`, `fieldset` stay). Add at the top (after the Tailwind imports):

```scss
:root,
.app-dark {
  --bg:        #0f141b;
  --topbar:    #141b24;
  --surface:   #1a212b;
  --border:    #2a3340;
  --head:      #e2e8f0;
  --label:     #94a3b8;
  --accent:    #6366f1;
  --active:    #818cf8;
  --value:     #86efac;
  --value-set: #818cf8;
  --online:    #86efac;
  --resize:    #2dd4bf;

  /* severity bands: bg tint / left slice / bold text */
  --sev-error-bg:#2a1416;  --sev-error-slice:#ff5252;  --sev-error-text:#ff6b6b;
  --sev-warn-bg:#2a2110;   --sev-warn-slice:#ffa31a;   --sev-warn-text:#ffc24d;
  --sev-notice-bg:#1d1430; --sev-notice-slice:#b15cff; --sev-notice-text:#d8a2ff;
  --sev-info-bg:#0f2418;   --sev-info-slice:#2ee06a;   --sev-info-text:#5cf08a;

  /* category severity dots */
  --dot-quiet:#475569; --dot-ok:#34d399; --dot-warn:#fbbf24; --dot-err:#f87171;
}

body { background: var(--bg); color: var(--label); }
```

- [ ] **Step 2: Add the indigo focus ring for inputs.** Append:

```scss
input.p-inputtext:focus,
.p-inputtext:focus,
.p-select.p-focus,
.p-datepicker-input:focus {
  outline: none;
  border-color: var(--accent) !important;
  box-shadow: 0 0 0 3px rgba(99, 102, 241, 0.32) !important;
}
```

- [ ] **Step 3: Map the existing `event-level-*` classes to the severity bands** (these are produced by `levelName | lowercase` and used for category dots / row tints). Replace the old `@apply bg-*` event-level rules with band-driven colours:

```scss
/* category severity dot colour by worst level (CategoryModel.labelClass) */
.event-level-verbose, .event-level-trace, .event-level-debug, .event-level-info { color: var(--dot-ok); }
.event-level-notice { color: var(--dot-ok); }
.event-level-warn   { color: var(--dot-warn); }
.event-level-error, .event-level-severe, .event-level-critical,
.event-level-alert, .event-level-fatal, .event-level-emergency { color: var(--dot-err); }
```

- [ ] **Step 4: Verify build + visual.**

Run: `npm run build`
Expected: PASS (exit 0). Then `npm start`, open `localhost:4200`: app background is the slate `--bg`; no Material colour artefacts.

- [ ] **Step 5: Commit.**

```bash
git add src/styles.scss
git commit -m "feat(diagnostics-web): add muted-slate palette tokens + indigo focus ring"
```

---

## Task 2: App shell — splitter layout + mode toggle

**Files:**
- Modify: `src/app/app.component.html`, `src/app/app.component.scss`, `src/app/app.module.ts`

- [ ] **Step 1: Register PrimeNG modules.** In `app.module.ts` add to imports: `SplitterModule` (`primeng/splitter`), `SelectButtonModule` (`primeng/selectbutton`). Keep `FormsModule`.

- [ ] **Step 2: Rewrite `app.component.html` top bar + body.** Top bar: title (left), `appModel.mainMessage` (centre, class `mainMessageClass`), `appModel.titleMessage` (right). Mode toggle = `p-selectButton` bound to `appModel.tabIndex`:

```html
<div class="h-full w-full grid grid-rows-[auto_1fr] bg-[var(--bg)]">
  <div class="h-11 bg-[var(--topbar)] border-b border-[var(--border)] grid grid-cols-3 items-center px-3">
    <label class="text-[var(--head)] font-medium">Diagnostic Explorer</label>
    <button [class.invisible]="!appModel.mainMessage" (click)="appModel.mainMessageClick()"
            class="{{appModel.mainMessageClass}} justify-self-center">{{ appModel.mainMessage }}</button>
    <span class="text-xs justify-self-end text-[var(--label)]">{{ appModel.titleMessage }}</span>
  </div>

  <p-splitter [panelSizes]="[22, 78]" [minSizes]="[160, 0]" styleClass="h-full w-full border-0"
              stateKey="diag-main-split" stateStorage="local">
    <ng-template #panel>
      <div class="h-full grid grid-rows-[auto_1fr] overflow-hidden">
        <div class="p-1">
          <p-selectButton [options]="modes" [(ngModel)]="appModel.tabIndex"
                          optionLabel="label" optionValue="value" [allowEmpty]="false"
                          styleClass="w-full" />
        </div>
        <div class="overflow-hidden">
          <app-realtime-nav [class.hidden]="appModel.tabIndex !== 0" />
          <app-retro-nav [class.hidden]="appModel.tabIndex !== 1" />
        </div>
      </div>
    </ng-template>
    <ng-template #panel>
      <div class="h-full overflow-hidden">
        <app-realtime-display [class.hidden]="appModel.tabIndex !== 0" />
        <app-retro-display [class.hidden]="appModel.tabIndex !== 1" />
      </div>
    </ng-template>
  </p-splitter>
</div>
```

- [ ] **Step 3: Add `modes` to `app.component.ts`.**

```ts
modes = [ { label: 'Realtime', value: 0 }, { label: 'Retro', value: 1 } ];
```

(`appModel.tabIndex` already exists; `viewRealtime()/viewRetro()` just set it, so the two-way binding is equivalent. Remove the old `AngularSplitModule` usage here.)

- [ ] **Step 4: Remove leftover Material toolbar/button scss** from `app.component.scss` (keep any layout rules still referenced).

- [ ] **Step 5: Verify.** `npm run build` PASS. `npm start`: toggle is a full-width segmented control (Realtime active = indigo); dragging the splitter gutter resizes the sidebar; size persists on reload.

- [ ] **Step 6: Commit.**

```bash
git add src/app/app.component.html src/app/app.component.scss src/app/app.module.ts
git commit -m "feat(diagnostics-web): shell on p-splitter + p-selectButton mode toggle"
```

---

## Task 3: Realtime process list — resizable columns + palette

**Files:**
- Modify: `src/app/realtime-nav/realtime-nav.component.html`, `.scss`

- [ ] **Step 1: Enable resizable columns on the `p-table`.** Add `[resizableColumns]="true" columnResizeMode="expand"` to `<p-table>` and `pResizableColumn` to each `<th>`. Keep the existing filter (`p-iconfield` + `pInputText`), `p-checkbox` "Online", and `p-contextMenu` (Retro/Delete).

- [ ] **Step 2: Apply palette + online colour.** Row text uses `--label`; online rows (`getProcess(item).state === 'Online'`) use `--online`; selected/active row (`item === model.activeProcess`) gets `background: rgba(255,255,255,.04)`. Replace any hard-coded greys with tokens in `.scss`:

```scss
:host ::ng-deep .p-datatable-tbody > tr > td { border-bottom-color: var(--border) !important; }
```

- [ ] **Step 3: Verify.** `npm run build` PASS, `npm run lint` PASS. `npm start`, Realtime tab: process list renders; drag a column edge to resize; online process is lime; filter + online + right-click menu still work.

- [ ] **Step 4: Commit.**

```bash
git add src/app/realtime-nav/
git commit -m "feat(diagnostics-web): resizable columns + palette on process list"
```

---

## Task 4: Category navigation — severity-dot list (replaces tabs-right)

**Files:**
- Create: `src/app/category-nav/category-nav.component.ts`, `.html`, `.scss`
- Modify: `src/app/app.module.ts` (declare component), `src/app/realtime-display/*` (use it — done in Task 7)

- [ ] **Step 1: Create the component.** It takes the categories and the selected index, emits selection. `category-nav.component.ts`:

```ts
import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CategoryModel } from '../Model/CategoryModel';

@Component({
  selector: 'app-category-nav',
  templateUrl: './category-nav.component.html',
  styleUrls: ['./category-nav.component.scss'],
  standalone: false,
})
export class CategoryNavComponent {
  @Input() categories: CategoryModel[] = [];
  @Input() selectedIndex = 0;
  @Output() selectedIndexChange = new EventEmitter<number>();

  select(i: number) { this.selectedIndexChange.emit(i); }
}
```

- [ ] **Step 2: `category-nav.component.html`** — a vertical list; each item shows a severity dot (driven by `cat.labelClass`, which is `event-level-<worst>` or '') and the name; active item highlighted:

```html
<div class="cat-list h-full overflow-auto">
  @for (cat of categories; track cat; let i = $index) {
    <div class="cat" [class.active]="i === selectedIndex" (click)="select(i)">
      <span class="dot {{ cat.labelClass || 'quiet' }}"></span>
      <span class="name">{{ cat.name }}</span>
    </div>
  }
</div>
```

- [ ] **Step 3: `category-nav.component.scss`** — indent guide, dots, active state:

```scss
:host { display:block; height:100%; border-left:1px solid var(--border); min-width:140px; }
.cat { display:flex; align-items:center; gap:8px; padding:6px 10px; font-size:12px; color:var(--label); cursor:pointer; }
.cat:hover { background: rgba(255,255,255,.03); }
.cat.active { background: rgba(255,255,255,.05); border-right:2px solid var(--active); color:var(--head); font-weight:600; }
.dot { width:8px; height:8px; border-radius:50%; flex:0 0 8px; background: var(--dot-quiet); }
/* severity dot colour — reuse the event-level-* foreground colours from styles.scss as background */
.dot.quiet { background: var(--dot-quiet); }
.dot.event-level-verbose,.dot.event-level-trace,.dot.event-level-debug,.dot.event-level-info,.dot.event-level-notice { background: var(--dot-ok); }
.dot.event-level-warn { background: var(--dot-warn); }
.dot.event-level-error,.dot.event-level-severe,.dot.event-level-critical,.dot.event-level-alert,.dot.event-level-fatal,.dot.event-level-emergency { background: var(--dot-err); }
```

- [ ] **Step 4: Declare** `CategoryNavComponent` in `app.module.ts` declarations.

- [ ] **Step 5: Verify build.** `npm run build` PASS (component compiles even though not yet wired — wiring is Task 7).

- [ ] **Step 6: Commit.**

```bash
git add src/app/category-nav/ src/app/app.module.ts
git commit -m "feat(diagnostics-web): severity-dot category-nav list component"
```

---

## Task 5: Realtime category panels — palette alignment

**Files:**
- Modify: `src/app/realtime-category/realtime-category.component.html`, `.scss`

- [ ] **Step 1: Align panel/fieldset/prop-grid to tokens.** Keep the existing `p-panel`(SubCat) → `p-fieldset`(PropGroup) → grid structure. Property name = `--label` (or `--value-set` when `prop.canSet`), value = `--value`. Settable name clickable (opens set-property dialog — unchanged). Replace hard-coded greys/limes with tokens.

- [ ] **Step 2: Verify.** `npm run build` PASS. `npm start`, select a process, pick a category with properties: panels/fieldsets render; names grey (settable blue), values lime.

- [ ] **Step 3: Commit.**

```bash
git add src/app/realtime-category/
git commit -m "style(diagnostics-web): align category panels to palette tokens"
```

---

## Task 6: Event-row severity treatment

**Files:**
- Modify: `src/app/realtime-events/realtime-events.component.html`, `.scss`

- [ ] **Step 1: Ensure the table markup carries severity + column structure.** Each `<tr>` already has `event-level-{{level|levelName|lowercase}}`. Columns: Id, Date, Level, Message (`<td class="lvl">` on Level).

- [ ] **Step 2: Replace the row colour scss with the band treatment** (`realtime-events.component.scss`). Map `event-level-*` to bands; bold severity-coloured Id/Date/Level; white semibold message; glowing left slice:

```scss
table { width:100%; border-collapse:collapse; font-size:11px; }
th { text-align:left; background:var(--surface); color:var(--head); padding:3px 8px; position:sticky; top:0; }
td { padding:3px 8px; color:#cbd5e1; }
td:first-child, td:nth-child(2), td:nth-child(3) { font-weight:700; }
td:last-child { color:#fff; font-size:12.5px; font-weight:600; }
tr.event-row-selected { outline:2px solid var(--accent); outline-offset:-1px; filter:brightness(1.15); }
tbody tr { user-select:none; }

@mixin band($bg,$slice,$text) {
  td { background:$bg; }
  td:first-child { border-left:4px solid $slice; box-shadow: inset 7px 0 9px -6px $slice; }
  td:first-child, td:nth-child(2), td:nth-child(3) { color:$text; }
}
tr.event-level-error, tr.event-level-severe, tr.event-level-critical,
tr.event-level-alert, tr.event-level-fatal, tr.event-level-emergency { @include band(var(--sev-error-bg), var(--sev-error-slice), var(--sev-error-text)); }
tr.event-level-warn { @include band(var(--sev-warn-bg), var(--sev-warn-slice), var(--sev-warn-text)); }
tr.event-level-notice { @include band(var(--sev-notice-bg), var(--sev-notice-slice), var(--sev-notice-text)); }
tr.event-level-verbose, tr.event-level-trace, tr.event-level-debug, tr.event-level-info { @include band(var(--sev-info-bg), var(--sev-info-slice), var(--sev-info-text)); }
```

- [ ] **Step 3: Set selected-row class.** Bind `[class.event-row-selected]="item.isSelected"` on the row (replace any old `[class.font-bold]`).

- [ ] **Step 4: Verify.** `npm run build` PASS. `npm start`, an event sink with events: rows show calm tint + glowing left slice + bold coloured Id/Date/Level + white message; selecting a row outlines it indigo.

- [ ] **Step 5: Commit.**

```bash
git add src/app/realtime-events/
git commit -m "feat(diagnostics-web): punchy severity event-row treatment"
```

---

## Task 7: Realtime display — category-nav + content/detail splitter

**Files:**
- Modify: `src/app/realtime-display/realtime-display.component.html`, `.scss`, `.ts`
- Modify: `src/app/app.module.ts` (SplitterModule already added Task 2)

- [ ] **Step 1: Rewrite the display layout.** Replace the `tabs-right` p-tabs with: the active category's `app-realtime-category` in the content area, the `app-category-nav` on the right, and a vertical `p-splitter` between content and the detail panel. Keep the empty-state hint.

```html
<div class="h-full">
  @if (model.categories.length) {
    <p-splitter layout="vertical" [panelSizes]="[72, 28]" [minSizes]="[0, 0]"
                styleClass="h-full border-0" stateKey="diag-detail-split" stateStorage="local">
      <ng-template #panel>
        <div class="h-full flex overflow-hidden">
          <div class="flex-1 overflow-auto p-2">
            <app-realtime-category [category]="model.categories[model.selectedIndex]" />
          </div>
          <app-category-nav class="flex-none" [categories]="model.categories"
                            [(selectedIndex)]="model.selectedIndex"
                            (selectedIndexChange)="model.handleSelectedTabChanged($event)" />
        </div>
      </ng-template>
      <ng-template #panel>
        @if (model.traceScopeVisible && model.selectedEvent) {
          <app-event-detail [event]="model.selectedEvent" (closed)="model.hideTraceScope()" />
        } @else {
          <div class="h-full flex items-center justify-center text-[var(--label)] italic text-sm">No event selected</div>
        }
      </ng-template>
    </p-splitter>
  } @else {
    <div class="h-full flex items-center justify-center text-[var(--label)] italic px-4 text-center">
      {{ model.activeProcess ? 'Waiting for diagnostics…' : 'Select a process from the list to view diagnostics.' }}
    </div>
  }
</div>
```

- [ ] **Step 2: Slim `realtime-display.component.scss`** — remove the old `.tabs-right`/`.scope-tabs`/`.vertical-tabs` rules (now obsolete). Keep `:host{display:block;height:100%}`.

- [ ] **Step 3: Keep `onCategoryTab` only if still referenced; otherwise remove** (selection now flows through `[(selectedIndex)]` + `handleSelectedTabChanged`). Ensure `realtime-display.component.ts` compiles.

- [ ] **Step 4: Remove the `.tabs-right` global recipe** from `src/styles.scss` (no longer used).

- [ ] **Step 5: Verify.** `npm run build` PASS. `npm start`: category list on the right with severity dots; clicking a category swaps content; selecting an event opens the detail splitter at the bottom; drag the horizontal gutter to resize; sizes persist.

- [ ] **Step 6: Commit.**

```bash
git add src/app/realtime-display/ src/styles.scss
git commit -m "feat(diagnostics-web): realtime display with category-nav + content/detail splitter"
```

---

## Task 8: Trace Scope tree renderer (TDD) + tabbed event-detail

**Files:**
- Create: `src/app/trace-scope/trace-scope.component.ts`, `.html`, `.scss`, `trace-scope.component.spec.ts`
- Create: `src/app/event-detail/event-detail.component.ts`, `.html`, `.scss`
- Modify: `src/app/app.module.ts` (declare both)
- Delete: `src/app/collapsible-region/*` (after wiring)

- [ ] **Step 1: Write the failing test for the tree renderer.** `trace-scope.component.spec.ts` — render a parsed `ScopeNode` and assert it shows a node per BEGIN region, nested, collapsible:

```ts
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TraceScopeComponent } from './trace-scope.component';
import { ScopeNode } from '../Model/ScopeNode';

const RAW = `[00.000] [00.042] BEGIN ProcessOrder
  [00.001] [00.003] BEGIN Validate
    CheckLimits: ok
  [00.004] [00.003] END Validate
[00.042] [00.042] END ProcessOrder`;

describe('TraceScopeComponent', () => {
  let fixture: ComponentFixture<TraceScopeComponent>;
  beforeEach(async () => {
    await TestBed.configureTestingModule({ declarations: [TraceScopeComponent] }).compileComponents();
    fixture = TestBed.createComponent(TraceScopeComponent);
  });

  it('renders a summary per BEGIN region and nests children', () => {
    fixture.componentInstance.node = ScopeNode.parseTraceScope(RAW)!;
    fixture.detectChanges();
    const summaries = fixture.nativeElement.querySelectorAll('summary.scope');
    expect(summaries.length).toBe(2);                 // ProcessOrder + Validate
    expect(fixture.nativeElement.textContent).toContain('ProcessOrder');
    expect(fixture.nativeElement.textContent).toContain('Validate');
  });

  it('shows leaf lines under their scope', () => {
    fixture.componentInstance.node = ScopeNode.parseTraceScope(RAW)!;
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.leaf')?.textContent).toContain('CheckLimits');
  });
});
```

- [ ] **Step 2: Run it — expect FAIL** (component doesn't exist).

Run: `npx jest trace-scope --runInBand`
Expected: FAIL (cannot find `TraceScopeComponent`).

- [ ] **Step 3: Implement `trace-scope.component.ts`.** Recursive component over `ScopeNode`. Parse the `[ss.mmm] [ss.mmm] BEGIN Label` firstLine into start-offset + duration + label for display.

```ts
import { Component, Input } from '@angular/core';
import { ScopeNode } from '../Model/ScopeNode';

@Component({
  selector: 'app-trace-scope',
  templateUrl: './trace-scope.component.html',
  styleUrls: ['./trace-scope.component.scss'],
  standalone: false,
})
export class TraceScopeComponent {
  @Input() node?: ScopeNode;

  // "[00.005] [00.028] BEGIN Persist" -> { start:'0.005', dur:28, label:'Persist' }
  parse(firstLine: string): { start: string; dur: number; label: string } {
    const m = /^\[(\d{2})\.(\d{3})\]\s*\[(\d{2})\.(\d{3})\]\s*BEGIN\s*(.*)$/.exec(firstLine ?? '');
    if (!m) return { start: '', dur: 0, label: firstLine ?? '' };
    const start = `${parseInt(m[1], 10)}.${m[2]}`;
    const dur = parseInt(m[3], 10) * 1000 + parseInt(m[4], 10);
    return { start, dur, label: m[5].trim() };
  }
  isBig(dur: number): boolean { return dur >= 20; }
}
```

- [ ] **Step 4: Implement `trace-scope.component.html`.** A BEGIN node (`node.isBegin`) renders a `<details>` with a styled `<summary>` + recursive children; a non-BEGIN node renders its text as a `.leaf`:

```html
@if (node) {
  @if (node.isBegin) {
    <details [open]="node.expanded || node.level === 1" (toggle)="node.expanded = $any($event.target).open">
      <summary class="scope">
        @if (parse(node.firstLine); as p) {
          <span class="t0">{{ p.start }}</span>
          <span class="dur" [class.big]="isBig(p.dur)">{{ p.dur }}ms</span>
          <span class="lbl">{{ p.label }}</span>
        }
      </summary>
      <div class="children">
        @for (child of node.childRegions; track child) {
          <app-trace-scope [node]="child" />
        }
      </div>
    </details>
  } @else {
    <div class="leaf">{{ node.getDisplayText() }}</div>
  }
}
```

- [ ] **Step 5: Implement `trace-scope.component.scss`** (tokens + indent guides + chevrons):

```scss
:host { display:block; }
details { margin:0; }
summary.scope { list-style:none; cursor:pointer; display:flex; align-items:center; gap:8px; padding:2px 4px; border-radius:4px;
  font-family: ui-monospace, Consolas, monospace; }
summary.scope::-webkit-details-marker { display:none; }
summary.scope:hover { background: rgba(255,255,255,.04); }
summary.scope::before { content:'▸'; color:var(--label); font-size:10px; width:10px; display:inline-block; transition: transform .12s; }
details[open] > summary.scope::before { transform: rotate(90deg); }
.children { margin-left:13px; border-left:1px solid var(--border); padding-left:13px; }
.t0 { color:var(--label); font-size:10px; }
.dur { font-size:10px; color:#a5b4fc; background:#191f2e; border:1px solid var(--border); border-radius:3px; padding:0 5px; }
.dur.big { color:var(--sev-error-text); border-color:#3a2226; }
.lbl { color:var(--head); }
.leaf { padding:1px 0 1px 22px; color:var(--label); font-family: ui-monospace, Consolas, monospace; font-size:11px; }
```

- [ ] **Step 6: Declare `TraceScopeComponent` in `app.module.ts`. Run the test — expect PASS.**

Run: `npx jest trace-scope --runInBand`
Expected: PASS (2 tests).

- [ ] **Step 7: Create `event-detail` (tabbed Detail / Trace Scope).** `event-detail.component.ts`:

```ts
import { Component, EventEmitter, Input, Output } from '@angular/core';
import { EventModel } from '../Model/EventModel';

@Component({
  selector: 'app-event-detail',
  templateUrl: './event-detail.component.html',
  styleUrls: ['./event-detail.component.scss'],
  standalone: false,
})
export class EventDetailComponent {
  @Input() event?: EventModel;
  @Output() closed = new EventEmitter<void>();
}
```

`event-detail.component.html` (uses `p-tabs`; Trace Scope shows `app-trace-scope` when `event.region` exists):

```html
<div class="h-full flex flex-col bg-[var(--topbar)] border-t-2 border-[var(--accent)]">
  <p-tabs value="detail" class="flex-1 flex flex-col min-h-0">
    <p-tablist>
      <p-tab value="detail">Detail</p-tab>
      <p-tab value="trace">Trace Scope</p-tab>
      <button class="ml-auto mr-2 text-[var(--label)] hover:text-white" (click)="closed.emit()">✕</button>
    </p-tablist>
    <p-tabpanels class="flex-1 min-h-0 overflow-auto">
      <p-tabpanel value="detail">
        <textarea class="w-full h-full bg-[#0f172a] text-[#86efac] font-mono text-xs resize-none p-2"
                  readonly [value]="event?.displayText ?? ''"></textarea>
      </p-tabpanel>
      <p-tabpanel value="trace">
        @if (event?.region) {
          <app-trace-scope [node]="event!.region" class="block p-2" />
        } @else {
          <div class="p-2 text-[var(--label)] italic">No trace scope for this event.</div>
        }
      </p-tabpanel>
    </p-tabpanels>
  </p-tabs>
</div>
```

- [ ] **Step 8: Declare `EventDetailComponent`; ensure `TabsModule` imported (already from earlier).** `npm run build` PASS.

- [ ] **Step 9: Delete the old renderer.** Remove `src/app/collapsible-region/*` and its declaration from `app.module.ts` (the `realtime-display` reference was already replaced in Task 7 with `app-event-detail`). `npm run build` PASS.

- [ ] **Step 10: Verify visually.** `npm start`, select an event whose detail contains a `BEGIN/END` trace: the detail panel shows Detail + Trace Scope tabs; Trace Scope renders the collapsible tree (chevrons, indent guides, duration badges red for ≥20ms, leaf lines); nodes expand/collapse.

- [ ] **Step 11: Commit.**

```bash
git add src/app/trace-scope/ src/app/event-detail/ src/app/app.module.ts
git rm -r src/app/collapsible-region
git commit -m "feat(diagnostics-web): restore Trace Scope as a proper collapsible tree + tabbed event-detail"
```

---

## Task 9: Retro search form

**Files:**
- Modify: `src/app/retro-nav/retro-nav.component.html`, `.scss`
- Modify: `src/app/app.module.ts` (add `SelectModule`, `DatePickerModule`, `ButtonModule`, `ProgressBarModule`)

- [ ] **Step 1: Register modules.** `app.module.ts`: `SelectModule` (`primeng/select`), `DatePickerModule` (`primeng/datepicker`), `ProgressBarModule` (`primeng/progressbar`); `ButtonModule` already present.

- [ ] **Step 2: Rebuild the form with PrimeNG controls**, bound to the same `model` fields (`maxRecords`, `minLevel`, `machine`, `process`, `user`, `message`, `date`, `time`, `hours`) and methods (`reset()`, `delete()`, `search()`). Two-up rows for Max Records/Min Level and Time/Window. Text inputs use `pInputText` (get the indigo focus ring from Task 1). Example field:

```html
<div class="field">
  <label class="lbl">Max Records</label>
  <p-select [(ngModel)]="model.maxRecords" [options]="maxRecordOptions"
            optionLabel="label" optionValue="value" styleClass="w-full" />
</div>
<div class="field">
  <label class="lbl">Machine</label>
  <input pInputText [(ngModel)]="model.machine" class="w-full" placeholder="any" />
</div>
```

Add the options arrays to `retro-nav.component.ts` (`maxRecordOptions`, `minLevelOptions`, the existing `times`/`hours`). Progress bar: `@if (model.currentSearchId) { <p-progressbar [value]="(model.percentComplete ?? 0) * 100" /> }`. Buttons row: Reset / Delete (`[disabled]="!model.canDelete"`) / Search (label `model.currentSearchId ? 'Cancel' : 'Search'`).

- [ ] **Step 3: `.scss`** — `.field{display:flex;flex-direction:column;gap:2px}`, `.lbl{font-size:9px;text-transform:uppercase;letter-spacing:.05em;color:var(--label)}`, form padding/gap. No internal scroll forced (rail scrolls only if window too short).

- [ ] **Step 4: Verify.** `npm run build` PASS, `npm run lint` PASS. `npm start`, Retro tab: all fields render as PrimeNG controls; clicking a text box shows the indigo focus ring; Search/Reset/Delete present; selecting a date works.

- [ ] **Step 5: Commit.**

```bash
git add src/app/retro-nav/ src/app/app.module.ts
git commit -m "feat(diagnostics-web): Retro search form on PrimeNG controls + focus ring"
```

---

## Task 10: Retro results table + shared detail

**Files:**
- Modify: `src/app/retro-display/retro-display.component.html`, `.scss`, `.ts`

- [ ] **Step 1: Rebuild as `p-splitter` (vertical): results table over `app-event-detail`.** Results = `p-table` (`[resizableColumns]="true"`) over `model.displayResults`, columns Date · Level · Machine · User · Process · Message; sticky header; rows carry `event-level-{{item.level|levelName|lowercase}}` and `[class.event-row-selected]="item.isSelected"`; `(mousedown)="model.setCurrentEvent(item)"` + `(mouseover)="model.handleMouseOver(item,$event)"`. Header shows `Retro Results · {{model.resultsMessage}}` + the existing `app-event-filter`.

- [ ] **Step 2: Reuse the severity-row scss.** Extract the Task-6 band scss into a shared partial `src/styles.scss` rule keyed on a common class (e.g. `.sev-table tr.event-level-*`), and apply `.sev-table` to both the realtime-events table and the retro results table, so the treatment is defined once (DRY). Update Task-6's table to use `.sev-table` too.

- [ ] **Step 3: Bottom panel = `app-event-detail`** bound to `model.selectedEvent`, shown when `model.traceScopeVisible`.

- [ ] **Step 4: Verify.** `npm run build` PASS. `npm start`, Retro tab, run a search (or with seeded data): results table shows severity rows; columns resizable; clicking a row opens the shared Detail/Trace-Scope panel.

- [ ] **Step 5: Commit.**

```bash
git add src/app/retro-display/ src/styles.scss src/app/realtime-events/
git commit -m "feat(diagnostics-web): Retro results table with shared severity rows + event-detail"
```

---

## Task 11: Persist splitter sizes (already via stateStorage) — verify & document

**Files:**
- Modify: (none if `p-splitter stateStorage="local"` covers it)

- [ ] **Step 1: Confirm persistence.** `p-splitter` with `stateKey`/`stateStorage="local"` (Tasks 2 & 7) persists pane sizes. `p-table` column widths persist with `stateKey`/`stateStorage="local"` on the table — add these to the process, results, and event tables.

- [ ] **Step 2: Verify.** `npm start`, resize sidebar + a column + the detail split, reload: sizes retained.

- [ ] **Step 3: Commit.**

```bash
git add src/app/realtime-nav/ src/app/retro-display/ src/app/realtime-events/
git commit -m "feat(diagnostics-web): persist splitter + column sizes to localStorage"
```

---

## Task 12: Remove Material + restore bundle budget

**Files:**
- Modify: `package.json`, `angular.json`, `src/app/app.module.ts`, `src/styles.scss`

- [ ] **Step 1: Confirm no Material usage remains.**

Run: `npx grep -rl "@angular/material" src` (or `git grep -l "@angular/material" -- src`)
Expected: no results. If any, convert them before continuing. (CDK `Clipboard` in `realtime-category.ts`/`ExecOperationsModel.ts` stays — `@angular/cdk` is kept.)

- [ ] **Step 2: Remove Material modules** from `app.module.ts` imports (MatTabs/Sidenav/Toolbar/Icon/Button/Table/Input/List/Expansion/Card/Tooltip/SnackBar/Checkbox/Dialog/Menu/Select/Datepicker/NativeDate/ProgressBar) and the `MAT_DATE_LOCALE` provider. Remove `@angular/material` from `package.json`. Remove the `@use '@angular/material'` + any remaining `mat.*` from `src/styles.scss`.

> Note: dialogs (set-property, info, exec-operations, snackbars) still use `MatDialog`/`MatSnackBar` in the data layer. If those are still present, convert them to PrimeNG `DialogService`/`MessageService` **as part of this task** (they are the last Material consumers). Check `RealtimeModel`, `realtime-category.ts`, `set-property-dialog`, `info-dialog`, `exec-operations`. Each: swap `MatDialog.open(...)` → `DialogService.open(...)`, `MatSnackBar.open(...)` → `MessageService.add(...)` + a `p-toast` in `app.component`.

- [ ] **Step 3: Reinstall + restore budget.** `npm install` (drops Material). In `angular.json`, set initial bundle `maximumError` back to `2mb` (was raised to `4mb` for the transition).

- [ ] **Step 4: Verify.** `npm run build` PASS and **within the 2mb budget** (no budget error). `npm run lint` PASS.

- [ ] **Step 5: Commit.**

```bash
git add package.json package-lock.json angular.json src/app/app.module.ts src/styles.scss src/app/
git commit -m "chore(diagnostics-web): remove Angular Material; restore 2mb bundle budget"
```

---

## Task 13: Quality gate

**Files:** (test/config only)

- [ ] **Step 1: Run the full Jest suite.**

Run: `npm test`
Expected: all pass (incl. the new `trace-scope` spec). Fix any specs broken by template/markup changes (update selectors, not behaviour).

- [ ] **Step 2: Lint.**

Run: `npm run lint`
Expected: clean (no ESLint/SonarJS errors). Fix inline.

- [ ] **Step 3: Production build within budgets.**

Run: `npm run build`
Expected: exit 0, no budget errors.

- [ ] **Step 4: Visual smoke test.** `npm start`: Realtime (process select → categories with severity dots → panels + events → event select → Detail/Trace-Scope tree), Retro (search → results → event select → detail), resize everything, reload (sizes persist).

- [ ] **Step 5: Commit any fixes.**

```bash
git add -A
git commit -m "test(diagnostics-web): green Jest + lint + prod build for UI redesign"
```

- [ ] **Step 6: CodeQL note.** CodeQL runs in CI on the PR (web-UI export per `~/.claude/notes/codeql-triage.md`); triage any findings after the PR build, not in this plan.

---

## Self-review (done at write time)

- **Spec coverage:** framework decision (Task 0/2 — PrimeNG kept, Material removed Task 12); Layout A + splitters (Tasks 2,7,10); palette tokens (Task 1); event-row treatment (Tasks 6,10); category-nav severity list (Tasks 4,7); detail panel + Trace Scope tree restore (Task 8); Retro form + results (Tasks 9,10); indigo focus (Task 1); resize + persistence (Tasks 2,3,7,10,11); empty/loading states (Tasks 7,9); quality gates (Task 13). All spec sections map to a task.
- **Deferred (per spec §1):** Azure auth, Docker SPA repoint — not in this plan, intentionally.
- **Type consistency:** `ScopeNode` API used (`isBegin`, `firstLine`, `childRegions`, `level`, `expanded`, `getDisplayText()`) matches `Model/ScopeNode.ts`; `EventModel.region`/`displayText` exist; `RealtimeModel.selectedIndex`/`handleSelectedTabChanged`/`traceScopeVisible`/`selectedEvent` exist; `RetroModel` fields match `retro-nav` bindings.
- **Open defaults (spec §15):** critical/alert/fatal/emergency reuse the error band (Task 6 includes them); sizes persisted (Task 11); category strip lives in a `flex-none` slot (resizable can be added later if wanted).
