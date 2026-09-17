import Link from "next/link";

export function SiteFooter() {
  return (
    <footer className="relative z-10 border-t border-white/6 bg-bg-0/55">
      <div className="mx-auto flex max-w-[1264px] flex-wrap items-center justify-between gap-3 px-5 py-4">
        <div className="flex flex-col gap-0.5">
          <span className="font-mono text-xs uppercase tracking-[.08em] text-muted/90">
            Copyright (c) 2026 EveDeck
          </span>
          <span className="font-mono text-[.68rem] uppercase tracking-[.08em] text-muted/50">
            EVE Online is a trademark of Fenris Creations hf.
          </span>
          <span className="font-mono text-[.68rem] uppercase tracking-[.08em] text-muted/50">
            ISK donations in-game to EveDeck.Space [EDECK]
          </span>
        </div>
        <div className="flex items-center gap-4">
          <a
            href="https://evedeck.space"
            className="text-xs text-muted/70 no-underline transition-colors hover:text-accent"
          >
            EveDeck
          </a>
          <Link href="/privacy" className="text-xs text-muted/70 no-underline transition-colors hover:text-accent">
            Privacy
          </Link>
          <Link
            href="/legal-notice"
            className="text-xs text-muted/70 no-underline transition-colors hover:text-accent"
          >
            Legal Notice
          </Link>
        </div>
      </div>
    </footer>
  );
}
