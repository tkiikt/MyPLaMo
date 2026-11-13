# MyPLaMo extension menu

## Identity and source

Stitch project: `9070954988566557537`
Final design screen: `bfddb9fec8484dec995f62f3da785ad5`
Design system: MyPLaMo Paper & Teal, `assets/7064065026620985160`.

## Theme

White (#ffffff) is the main surface. Pale gray-green (#f5f8f7) separates the
sidebar and page context; deep teal (#126b63) marks the main action and switches.
Charcoal (#263835) supplies primary text, muted green-gray (#667975) supporting
text, and fine borders (#e3eae7) separate groups. No gradients or decorative art.

## Typography and layout

Use local system fonts with Japanese fallbacks, 15px body text, 20px headings,
and readable captions (increased by 2px after user review). The root is explicitly 720 by 580 CSS pixels to preserve
Chrome action-popup intrinsic sizing. Use a 164px icon-and-label sidebar and a
scrollable main pane. At narrow widths the navigation moves above the content.

## Components and behavior

Group current-page context, language selection, glossary, the main translate
action and quick settings. Keep the original five panels and all existing IDs
and setting hooks. Rounded native selects/buttons and real checkbox switches
retain keyboard access. Connection status includes text as well as color.

## Implementation adaptations

The generated references under designs/ are visual source material. The shipped
popup uses local CSS, inline SVG icons, local app artwork and the existing
popup.js: no Tailwind CDN, remote font, generated script or new dependency.
Invented quotas, specialty dictionaries, shortcuts and window controls were
removed from the Stitch design before implementation. Other settings panels
inherit the same design tokens while retaining their existing operations.
