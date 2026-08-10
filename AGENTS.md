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
