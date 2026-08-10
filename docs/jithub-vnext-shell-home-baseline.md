# JitHub vNext Shell And Home Baseline

This document records the current vNext shell and Home design baseline. It is
the reference point for the remaining page redesigns. Future pages should build
on these contracts instead of reintroducing separate page chrome, visible
workspace tabs, browser-like refresh controls, or page-local repository rails.

## Product direction

JitHub should feel like a native productivity tool for GitHub work, not a web
page inside a window. The shell and Home page now establish that direction:

- One app shell with one content frame.
- One combined left rail for app navigation and repositories.
- One global command search.
- Home as a responsive widget board.
- App-owned modal overlays inside the shell visual tree.
- Stale-first data surfaces with background refresh.
- Dense but calm information layout with stable sizing.

The shell and Home are now the design system proving ground for the rest of the
vNext work.

## Shell contract

| Area | Current contract |
| --- | --- |
| Content model | The right side is a single `Frame`, not a visible `TabView`. Navigation replaces the active page in place. |
| Left rail | The app nav and repository list live in one rail. There is no separate repository panel. |
| Navigation items | Home, Issues, Pull Requests, Stars, Gists, Explore, and Settings. Projects stays hidden until a ProjectV2 plan exists. |
| Repository filters | Repository rail uses `Public`, `Private`, and `Forked` segmented filters. The separate rail search box and `Archived` chip are not part of the current baseline. |
| Repository items | Repo rows stay compact, do not repeat public/private captions, and use the rail filter for category context. |
| Global search | Top search is the command/repository entry point. `Ctrl+K` focuses it. Explore focuses search instead of opening a separate page. |
| Actions | Top actions stay compact and native. They should not read as web toolbar refresh controls. |
| Notifications | Notification nav is not exposed yet. Home can surface notifications and later phases can add a full page. |
| Modal behavior | Modal content is hosted by the shell overlay layer so dialogs are clipped to and centered in the app window. Do not use unconstrained popups for app dialogs. |

## Shell routes

| Route | Destination |
| --- | --- |
| Home | `DashboardPage` |
| Issues | My Issues, default query `is:issue is:open assignee:{login}` |
| Pull Requests | My Pull Requests, default query `is:pr is:open involves:{login}` |
| Stars | Starred repositories |
| Gists | Authenticated gists |
| Explore | Focus global command search |
| Settings | `SettingsPage` |
| Repository click | Repository code/detail surface in the single content frame |

Navigation highlight must match the current content route. A nav item should not
open a tab because that makes selected state ambiguous.

## Home widget board

Home uses a two-rail widget board rather than a full-page feed or tabbed
dashboard.

| Area | Contract |
| --- | --- |
| Board width | Center the board on wide screens with a max width around `1160px`. |
| Main rail | Recent activity, recent repositories, quick actions. This rail gets the most width. |
| Side rail | Overview, recommended repositories, notifications. |
| Compact behavior | At compact widths, side widgets collapse into an Overview drawer opened from the header. |
| Scrolling | The board may scroll vertically. Individual widgets should not expose their own scroll areas for normal preview content. The side rail may scroll independently when needed. |
| Widget height | Widgets use fixed preview heights and a `View all` action for full pages. Do not cram all data into Home. |
| Widget cards | Widget surfaces use the app dark green/black token system, subtle borders, and compact headers. |
| Captions | Avoid explanatory captions unless a spec explicitly asks for them. Titles and labels should carry the interface. |

## Home widgets

| Widget | Current behavior |
| --- | --- |
| Recent activity | Preview of merged activity events. Activity rows are not selectable as whole cards; inline links navigate to their targets. Event icons use type-specific colors. Times display as relative time. |
| Recent repositories | Responsive repo cards. Cards grow to fill available columns and reflow to one column at narrow widths. Use repo imagery where available, language dots, and no browser-style link-out icon. |
| Quick actions | Compact bordered actions with distinct icon colors. Actions route through shell contracts and show native shell notifications when an active repository is required. |
| Overview | Compact metrics without captions. Icon and label alignment should match the row baseline. |
| Recommended repositories | Simple one-line repository rows, not large cards. The section is called recommended repositories, not trending, because GitHub does not expose website Trending through REST. |
| Notifications | Preview rows must fit fully within the widget. No partial 3.1-item layouts. Full-page notification work is a later phase. |

## Widget customization

Home customization is part of the shell modal system.

| Behavior | Contract |
| --- | --- |
| Host | Use the shell modal overlay, not `Popup` or a standalone presenter that can escape the window. |
| Centering | Dialog is centered within the app window. |
| Surface | Use app token colors, not the default gray `ContentDialog` surface. |
| Controls | Show/hide toggle, move up, move down, move between main/side rail, reset, cancel, save. |
| Persistence | Save layout through `DashboardWidgetLayout.v1`. Reload must restore the saved layout exactly. |
| Recovery | Unknown widget ids are dropped, missing defaults are repaired, and corrupt layout JSON falls back to defaults. |

## Data behavior

Home and shell data should keep the Phase 0 stale-first contract:

- Cached rows render first.
- Refresh updates in place.
- A failed refresh does not blank existing content.
- Sections fail independently.
- Public preview/sample mode must not call authenticated endpoints.
- Telemetry must not include repository names, usernames, search text, URLs,
  titles, branches, markdown, code, commit messages, or tokens.

## Responsive rules

| Width class | Expected behavior |
| --- | --- |
| Wide desktop | Combined shell rail visible. Home main and side rails visible. Board centered. |
| Medium desktop | Rail and board remain usable. Widgets reflow without clipped text or right-edge cropping. |
| Narrow/snapped | Side rail collapses to drawer. Main rail stays usable. Shell chrome remains stable. |

The app can scroll when content truly exceeds the viewport, but it should not
feel like a website. Each view should prioritize the useful preview area and
route users to full pages for deeper work.

## Automation baseline

The automation project should keep these probes current:

| Probe | Purpose |
| --- | --- |
| `shell-responsive` | Captures shell/Home at common desktop and snapped sizes. |
| `shell-nav-clicks` | Verifies nav selection and destination pages. |
| `shell-hover-states` | Captures hover states for nav, actions, repo filters, repo rows, and widget actions. |
| `shell-search-states` | Verifies global search normal, hover, focused, and repo filter states. |
| `shell-repo-click` | Verifies repository selection routes to repo content and marks the active repo. |
| `home-widget-board` | Verifies two-rail layout and compact drawer behavior. |
| `home-customize` | Verifies customize modal opens, remains inside the app window, stays centered, reset/save works, and closes. |
| `home-view-all` | Verifies widget `View all` actions route correctly. |
| `command-search` | Verifies `Ctrl+K`, suggestions, execution, Explore focus, and Escape dismissal. |

Screenshots are written under `artifacts/screenshots/winui-vnext-shell`.

## Design rules for remaining pages

Future page phases should follow these rules:

- Reuse the shell content frame.
- Do not add page-local nav rails or repository rails.
- Do not add visible workspace tabs until a separate tab design is planned.
- Do not add manual refresh buttons as primary UI. Prefer quiet background
  refresh and explicit retry only for errors.
- Use shell-level modal overlays for app dialogs.
- Keep action icons native and avoid browser metaphors such as link-out icons
  unless an action truly opens an external browser.
- Keep content preview widgets fixed and route deeper work to full pages.
- Keep selected state, focus, and scroll position stable during refresh.
