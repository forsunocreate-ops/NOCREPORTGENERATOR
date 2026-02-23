# NOC Report Generator UI Style Guide

## 1. Visual Direction
- Theme: modern NOC command workspace, clean and high-signal.
- Personality: professional, data-dense, calm but responsive.
- Interaction target: fast scanning first, deep analysis second.

## 2. Color System
Use existing shell tokens from `App.xaml` as the foundation:
- Background: `ShellBackgroundBrush`
- Surface: `ShellPanelBackgroundBrush`, `ShellPanelSecondaryBackgroundBrush`
- Border: `ShellPanelBorderBrush`
- Primary action: `ShellPrimaryButtonGradientBrush`
- Text primary/muted: `ShellTitleForegroundBrush`, `ShellMutedForegroundBrush`
- Status base: `ShellDangerBrush`

Dashboard-specific accents (local page tokens in `DashboardPage.xaml`):
- Hero: `DashboardHeroSurfaceBrush`
- Elevated subtle card: `DashboardSubtleCardBrush`
- Positive: `DashboardPositiveBrush`
- Risk: `DashboardRiskBrush`
- Critical: `DashboardCriticalBrush`

## 3. Typography
- Base font: `ShellBaseFontFamily`.
- Display font: `ShellDisplayFontFamily`.
- Hierarchy:
  - Hero title: large display style (`ShellHeroTitleTextStyle`).
  - Section title: `DashboardSectionTitleStyle`.
  - Section/supporting text: `DashboardSectionCaptionStyle`.
  - KPI values: bold numeric emphasis (`DashboardMetricValueStyle`).

## 4. Layout, Spacing, Radius
- Page max width: 1760 px on desktop (`DashboardRootGrid MaxWidth`).
- Section spacing: 16 px vertical rhythm.
- Card corner radius:
  - Main sections: 18 px.
  - Sub-cards/KPI cards: 16 px.
- Internal padding:
  - Section cards: 18 px.
  - Sub-cards: 14 px.

## 5. Component Patterns
- Hero block:
  - Gradient surface with concise context text.
  - Operational chips for quick capability signaling.
  - Visual anchor icon for module recognition.
- KPI grid:
  - Uniform card rhythm.
  - Large values with clear label/value contrast.
- Analytics sections:
  - Always pair title + short caption + chart/list body.
- Search and filter row:
  - Keep controls in one row for desktop.
  - Preserve horizontal scroll fallback for narrow widths.

## 6. Motion and Feedback
- Use subtle entrance transitions only (`EntranceThemeTransition`, `NavigationThemeTransition`).
- Avoid excessive micro-animation; prioritize legibility.
- Keep refresh actions explicit and immediate.

## 7. Responsiveness
- Preserve horizontal scroll wrappers for data-heavy areas.
- Do not hide critical KPI information on compact width.
- Keep section order stable across widths to reduce cognitive load.

## 8. Implementation Rules for New Pages
- Reuse shell tokens first (`App.xaml`), add page-local tokens only when needed.
- Apply the same three-tier structure:
  - Hero/context
  - KPI/summary
  - Analysis/detail
- Favor consistent spacing and card shapes over one-off styling.
- Any new status color must map to positive/risk/critical semantics.
