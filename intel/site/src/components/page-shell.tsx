import type { ReactNode } from "react";

type PageShellProps = {
  title: string;
  kicker?: string;
  intro?: string;
  children: ReactNode;
};

export function PageShell({ title, kicker, intro, children }: PageShellProps) {
  return (
    <section className="mx-auto max-w-[1264px] px-5 py-10 sm:py-14">
      <div className="overflow-hidden rounded-panel border border-white/6 bg-gradient-to-b from-panel/85 to-panel-2/80 shadow-[0_18px_60px_rgba(0,0,0,.55)] backdrop-blur-sm">
        <div className="border-b border-white/6 bg-gradient-to-b from-white/5 to-transparent px-6 py-6 sm:px-8">
          <div className="mb-2 text-xs font-semibold uppercase tracking-[.22em] text-muted/70">
            {kicker ?? "EVEDECK"}
          </div>
          <h1 className="font-display text-2xl font-bold tracking-tight text-text sm:text-3xl">{title}</h1>
          {intro ? <p className="mt-3 max-w-[70ch] text-muted">{intro}</p> : null}
        </div>

        <div className="px-6 py-8 sm:px-8">
          <div
            className="prose prose-invert max-w-none
              prose-headings:font-display prose-headings:tracking-tight
              prose-h2:mt-8 prose-h2:mb-3 prose-h2:text-xl
              prose-p:text-muted prose-li:text-muted
              prose-a:text-accent prose-a:no-underline prose-a:border-b prose-a:border-accent/30 hover:prose-a:border-accent/60
              prose-strong:text-text
              prose-code:text-accent prose-code:before:content-none prose-code:after:content-none prose-code:bg-accent/10 prose-code:px-1.5 prose-code:py-0.5 prose-code:rounded-md prose-code:font-normal
              prose-pre:bg-bg-0/75 prose-pre:border prose-pre:border-white/6 prose-pre:rounded-2xl
              prose-hr:border-white/6"
          >
            {children}
          </div>
        </div>
      </div>
    </section>
  );
}
