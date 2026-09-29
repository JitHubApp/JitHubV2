namespace MarkdownRenderer.Sample;

internal static partial class SampleDocuments
{
    internal const string MathSample = """
        # Native mathematics

        The math pack renders TeX as native vector scenes. Every example below
        participates in selection, copy, text scaling, themes, and UI Automation.

        ## Inline expressions

        Basic algebra: $E = mc^2$, $a^2+b^2=c^2$, and $\frac{1}{2}$.

        Greek letters: $\alpha + \beta = {\gamma}$, $\Delta x$, and $\omega_0$.

        Radicals: $\sqrt{x^2+y^2}$ and $\sqrt[3]{8}=2$.

        Scripts and accents: $a_{i,j}^{(n)}$, $\vec{v}$, $\hat{x}$, and $\overline{AB}$.

        Functions and limits: $\sin^2\theta+\cos^2\theta = 1$, $\log_b x$, and
        $\lim_{x\to0}\frac{\sin x}{x}=1$.

        Sets and relations: $\forall x\in\mathbb{R}, x^2\ge0$ and
        $A\cap B\subseteq A\cup B$.

        Adaptive delimiters: $\left(\frac{a+b}{c+d}\right)$ and
        $\left|\vec{v}\right|$.

        Text inside an expression: $\text{speed}=\frac{\text{distance}}{\text{time}}$.

        ## Sums and fractions

        $$
        \sum_{i=1}^{n} i = \frac{n(n+1)}{2}
        $$

        ## Binomial theorem

        $$
        (1+x)^n = \sum_{k=0}^{n} \binom{n}{k}x^k
        $$

        ## Quadratic formula

        $$
        x = \frac{-b \pm \sqrt{b^2-4ac}}{2a}
        $$

        ## Integral with limits and spacing

        $$
        \int_{0}^{\infty} e^{-x}\,dx = 1
        $$

        ## Matrix multiplication

        $$
        \begin{bmatrix} a & b \\ c & d \end{bmatrix}
        \begin{bmatrix} x \\ y \end{bmatrix}
        =
        \begin{bmatrix} ax+by \\ cx+dy \end{bmatrix}
        $$

        ## Piecewise functions

        $$
        f(x)=\begin{cases}
        x^2, & x \ge 0 \\
        -x, & x < 0
        \end{cases}
        $$

        ## Aligned derivation

        $$
        \begin{aligned}
        (a+b)^2 &= a^2 + 2ab + b^2 \\
        (a-b)^2 &= a^2 - 2ab + b^2
        \end{aligned}
        $$

        Dollar delimiters are the supported 1.0 syntax. Escaped dollars such as
        \$20 remain text, and backslash delimiters remain literal: \(x+y\) and \[z\].
        """;

    // ── New feature sample pages ───────────────────────────────────────────────

    internal const string MermaidSample = """
        # Native Mermaid

        Mermaid source is parsed and laid out by the pinned native Merman engine,
        transferred as MMIR, and painted by Win2D. Diagrams participate in
        selection, themes, text scaling, and UI Automation without a browser.

        ## Flowchart: shapes, edges, subgraphs, classes, and links

        The first node exposes a real invokable UI Automation hyperlink. Authored
        class colors remain visible in normal themes and map to system colors in
        Windows High Contrast.

        ```mermaid
        flowchart LR
          subgraph Input["Input stage"]
            A(["Invokable Mermaid node"]) --> B[/"Markdown source"/]
          end
          B ==> C{Valid?}
          C -->|Yes| D[["Native scene"]]
          C -. Retry .-> B
          C -->|No| E[("Exact fallback")]
          classDef native fill:#d5f5e3,stroke:#1e8449,color:#145a32
          class D native
          click A "https://example.invalid/mermaid-node" "Open Mermaid node"
        ```

        ## Sequence diagram: actors, aliases, activation, notes, and alternatives

        ```mermaid
        sequenceDiagram
          autonumber
          actor User
          participant App as JitHub
          participant Renderer
          User->>App: Open README
          App->>+Renderer: Parse Markdown
          alt Supported syntax
            Renderer-->>App: Native semantic scene
          else Invalid syntax
            Renderer-->>-App: Exact fallback and diagnostic
          end
          Note over App,Renderer: No browser or JavaScript runtime
        ```

        ## Class diagram: members and relationships

        ```mermaid
        classDiagram
          direction LR
          class MarkdownEngine {
            +ParseAsync(source)
          }
          class Extension {
            <<interface>>
            +TryRender(node)
          }
          class MermaidExtension
          MarkdownEngine o-- Extension : composes
          Extension <|.. MermaidExtension : implements
        ```

        ## State diagram: transitions and a composite state

        ```mermaid
        stateDiagram-v2
          [*] --> Idle
          Idle --> Parsing : source changed
          Parsing --> Ready : success
          Parsing --> Fallback : diagnostic
          Ready --> Idle : edit
          Fallback --> Idle : edit
          state Ready {
            [*] --> Painted
            Painted --> Accessible
          }
        ```

        ## Entity relationship diagram: cardinality

        ```mermaid
        erDiagram
          DOCUMENT ||--o{ BLOCK : contains
          BLOCK ||--o| VECTOR_SCENE : renders_as
          VECTOR_SCENE ||--|{ SEMANTIC_NODE : exposes
          DOCUMENT {
            string source
            int version
          }
        ```

        ## Gantt chart: sections, dependencies, and task states

        ```mermaid
        gantt
          title Native rendering plan
          dateFormat YYYY-MM-DD
          section Pipeline
          Parse source :done, parse, 2026-09-01, 2d
          Build layout :active, layout, after parse, 3d
          Paint scene :paint, after layout, 2d
        ```

        ## Git graph: branches, checkout, merge, and tag

        ```mermaid
        gitGraph
          commit id: "parser"
          branch feature
          checkout feature
          commit id: "MMIR"
          checkout main
          merge feature tag: "v1.0"
        ```

        ## Pie chart: title and categorical values

        ```mermaid
        pie title Feature coverage
          "Native" : 75
          "Host mediated" : 20
          "Literal fallback" : 5
        ```

        ## User journey: sections, scores, and actors

        ```mermaid
        journey
          title README experience
          section Open
            Load file: 5: User
          section Render
            Parse markdown: 4: App
            Paint native scene: 5: Renderer
        ```

        ## Mind map: nested hierarchy

        ```mermaid
        mindmap
          root((MarkdownRenderer))
            Native
              Win2D
              DirectWrite
            Feature packs
              Math
              Mermaid
            Interaction
              Selection
              Touch
        ```

        ## Timeline: periods with multiple events

        ```mermaid
        timeline
          title Renderer milestones
          2024 : Core Markdown
          2025 : Profiles : Safe HTML
          2026 : Math : Mermaid : Touch selection
        ```

        ## XY chart: axes, bars, and a line series

        ```mermaid
        xychart-beta
          title "Frame budget"
          x-axis [Parse, Layout, Paint]
          y-axis "Milliseconds" 0 --> 20
          bar [5, 8, 3]
          line [5, 13, 16]
        ```

        ## Quadrant chart: axes, quadrants, and points

        ```mermaid
        quadrantChart
          title Native feature priorities
          x-axis Low effort --> High effort
          y-axis Low impact --> High impact
          quadrant-1 Strategic
          quadrant-2 Quick wins
          quadrant-3 Reconsider
          quadrant-4 Infrastructure
          Math: [0.55, 0.85]
          Mermaid: [0.72, 0.90]
          Touch: [0.42, 0.75]
        ```

        ## Requirement diagram: structured requirement metadata

        ```mermaid
        requirementDiagram
          requirement native_rendering {
            id: 1
            text: Render without a browser
            risk: high
            verifymethod: test
          }
        ```

        ## Sankey diagram: weighted flows

        ```mermaid
        sankey-beta

        Markdown,Parser,10
        Parser,Layout,8
        Parser,Fallback,2
        Layout,Win2D,8
        ```

        ## Packet diagram: bit ranges

        ```mermaid
        packet-beta
          0-3: "Version"
          4-7: "Flags"
          8-15: "Payload"
        ```

        ## Kanban: nested boards and cards

        This final diagram intentionally ends the document so selection can be
        tested without a following text block.

        ```mermaid
        kanban
          backlog[Backlog]
            parse[Parse source]
          active[Rendering]
            layout[Build layout]
          done[Done]
            paint[Paint scene]
        ```
        """;

    internal const string DiagramEmbedSample = """
        # Diagram extension pipeline

        A diagram pack claims only the fenced languages it understands. Here,
        `MarkdownRenderer.Mermaid` turns the normalized Mermaid fence into a native
        `DiagramBox`; it never hosts a browser, JavaScript runtime, or WinUI overlay.

        ```mermaid
        flowchart LR
            Parse[Immutable document] --> Plan[Display bands]
            Plan --> Layout[Off-thread layout]
            Layout --> Paint[Win2D paint]
            Paint --> UIA[TextPattern and fragments]
        ```

        The sample's custom hosted-element extension deliberately does not claim
        `diagram:sequence`, so this second fence remains visible as exact code.
        That fallback is the extension boundary working as designed:

        ```diagram:sequence
        participant Extension
        participant ContentBuilder
        participant HostFactory
        Extension->>ContentBuilder: AddHostedElement(factoryKey, sourceRange, role)
        ContentBuilder->>HostFactory: CreateAsync(viewport-aware request, cancellation)
        ```
        """;

    internal const string EmbedsSample = """
        # Declarative Hosted Elements (API preview)

        Stable extensions never receive parser nodes or Win2D lifetime objects.
        An extension emits a keyed request with
        `MarkdownContentBuilder.AddHostedElement`; a viewport-aware
        `IMarkdownHostedElementFactory` creates the WinUI element only when its
        immutable request is near the effective viewport.

        ```csharp
        content.AddHostedElement(
            "sample.button",
            context.Node.SourceSpan,
            new MarkdownStyleRole("SampleButton"),
            MarkdownAccessibilityRole.Group,
            context.Node.Attributes);
        ```

        This showcase installs a declarative extension and factory for the two
        extension-shaped fences below. They are realized as native WinUI
        elements only near the effective viewport and are recycled off-screen.

        ```button:Click me
        ```

        ```button:Another action
        ```

        ## Task lists use read-only document semantics by default

        - [x] Parser-independent declarative request
        - [x] Viewport and source range included in the factory request
        - [ ] This marker announces task state but is not a focusable checkbox

        A host that explicitly enables editable tasks must provide a real command
        or event; only then may the task expose `TogglePattern`.
        """;
}
