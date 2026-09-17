/**
 * How the two halves fit together.
 *
 * Inline SVG rather than a screenshot, and that is not a stylistic choice: a screenshot of a live
 * client carries real character names and real system names, which is exactly what must never end
 * up on a public page.
 */
export function FlowDiagram() {
  return (
    <figure className="not-prose overflow-hidden rounded-2xl border border-white/6 bg-bg-0/60 p-4">
      <svg
        viewBox="0 0 720 210"
        className="h-auto w-full"
        role="img"
        aria-label="EVE clients write chat logs to disk. The daemon reads them, parses them and measures distance, then serves a WebSocket on your LAN. The tablet connects to it and shows the feed, map and alerts."
      >
        <defs>
          <marker
            id="intel-arrow"
            viewBox="0 0 10 10"
            refX="9"
            refY="5"
            markerWidth="6"
            markerHeight="6"
            orient="auto"
          >
            <path d="M0,0 L10,5 L0,10 z" className="fill-accent" />
          </marker>
        </defs>

        <g className="fill-none stroke-white/10" strokeWidth="1">
          <rect x="8" y="30" width="180" height="150" rx="14" />
          <rect x="268" y="30" width="180" height="150" rx="14" />
          <rect x="532" y="30" width="180" height="150" rx="14" />
        </g>

        <g className="fill-muted" fontSize="11" fontFamily="ui-sans-serif, system-ui" letterSpacing="1.6">
          <text x="28" y="56">YOUR PC</text>
          <text x="288" y="56">YOUR PC</text>
          <text x="552" y="56">YOUR LAN</text>
        </g>

        <g className="fill-text" fontSize="15" fontFamily="ui-sans-serif, system-ui" fontWeight="600">
          <text x="28" y="100">EVE clients</text>
          <text x="288" y="100">Intel daemon</text>
          <text x="552" y="100">Tablet</text>
        </g>

        <g className="fill-muted" fontSize="12.5" fontFamily="ui-sans-serif, system-ui">
          <text x="28" y="124">write chat logs</text>
          <text x="28" y="144">to your Documents</text>
          <text x="288" y="124">reads, parses,</text>
          <text x="288" y="144">measures distance</text>
          <text x="552" y="124">feed, map,</text>
          <text x="552" y="144">alerts</text>
        </g>

        <g className="stroke-accent/70" strokeWidth="1.5" markerEnd="url(#intel-arrow)" fill="none">
          <path d="M196 105 L258 105" />
          <path d="M456 105 L522 105" />
        </g>

        <g className="fill-muted/70" fontSize="11" fontFamily="ui-sans-serif, system-ui" textAnchor="middle">
          <text x="227" y="96">files</text>
          <text x="489" y="96">WebSocket</text>
        </g>
      </svg>
    </figure>
  );
}
