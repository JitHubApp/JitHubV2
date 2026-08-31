# JitHub Agent Instructions

## Native App Layout Principles

JitHub is a Windows native productivity app, not a website. Page layout must be stable, responsive, and owned by the page/workspace shell rather than by whatever content happens to be visible.

- Do not allow a page, board, rail, tab, mode, or card to change the overall page width when selection/content changes.
- Use WinUI/Fluent controls for navigation and mode switching where they fit, such as `NavigationView`, `SelectorBar`, `TabView`, `ListView`, and `GridView`.
- Do not hand-roll fake tab/pivot controls out of unrelated buttons unless a platform control cannot satisfy the interaction and all states/accessibility are implemented.
- Prefer fixed structural panes plus flexible content panes. Structural width is a layout decision, not a child-content measurement side effect.
- Informational stats/tiles must not navigate to hidden modes. If clicking changes the page mode, that destination must be represented in visible navigation with a clear way back.
- Counts and lists must state their scope when multiple GitHub concepts could sound identical, such as public profile repositories, authenticated account repositories, recent previews, and filtered sidebar results.
- App pages should avoid website-like whole-page scrolling. Keep shell/identity/action regions fixed when practical and scroll only the active content region.
- Responsive behavior must preserve access to all panes/actions without adding permanent chrome that steals reading space.
- Test common snap widths and mode switches for no width shift, clipping, overlap, or surprise focus changes before considering UI work complete.

## WinUI Workflow And Inline Alignment

- Always load and follow the relevant Microsoft WinUI plugin skills for work in `JitHub.WinUI`: use the design skill before changing XAML, the development workflow for build/run diagnosis, code review before completion, and UI testing for live visual validation.
- An icon and its adjacent label must be peers in the same horizontal container and must both set `VerticalAlignment="Center"`. This includes `FontIcon`, `SymbolIcon`, `AppIcon`, `Avatar`, images, badges, status dots, and icon viewboxes.
- Do not align inline icons with top margins, local translations, or one-off pixel nudges. Fix the shared container and peer alignment first; when font metrics require optical correction, keep it in the shared catalog style/control, express it with a narrowly named token, and verify the rendered pixels at every supported scale.
- Adjacent text with different typography must share one `TextBlock` and use `Run` elements so WinUI lays it out on one baseline. This applies especially to title/count, title/caption, state/time, and identity/context pairs.
- List and card metadata rows must keep adjacent non-interactive values in one `TextBlock` with `Run` elements. Do not split hashes, authors, timestamps, languages, counts, or status text into separate `TextBlock`s solely to style them; keep any avatar, icon, or status dot as a centered peer.
- When separate text elements are required by interaction or responsive behavior, document and test the baseline strategy. Do not approximate a baseline with `Translation` or asymmetric margins.
- Preserve one coherent accessibility name when combining text into runs. Interactive content must remain a real control and must not be folded into decorative inline text.
- All spacing, typography, colors, dimensions, and motion must use app design tokens. Add a narrowly named token when an existing token does not express the intended value.
- Before closing UI work, inspect the rendered item template itself at 100%, 150%, and 200% scaling; parent alignment is not proof that child controls, font metrics, or internal padding are aligned.
- Keep `InlineAlignmentContractTests` current whenever an inline layout pattern or icon type is added. Verify alignment at 100%, 150%, and 200% scaling in light, dark, and High Contrast themes.
