import Link from "next/link";
import { FlowDiagram } from "@/components/flow-diagram";
import { DOWNLOADS, SOURCE_URL, VERSION } from "@/lib/release";

const features = [
  {
    title: "A feed you can read at a glance",
    body:
      "Every report, parsed into the system it names, the pilots in it and the hulls they are flying — instead of a wall of chat scrolling past behind your fleet window."
  },
  {
    title: "Jump distance that means something",
    body:
      "Measured on the real stargate graph from whichever character you are following, not guessed from the region. Three jumps out and four jumps out are different problems."
  },
  {
    title: "A map of where it is landing",
    body:
      "The reports plotted against your own position, so a quiet evening and a staging system filling up look different from across the desk."
  },
  {
    title: "Alerts inside a radius you set",
    body:
      "The tablet earns its place by telling you when something hostile is close enough to matter, while you are looking at the game and not at it."
  },
  {
    title: "No account, no API key, no cloud",
    body:
      "It reads the log files EVE already writes and serves them on your own network. There is nothing to sign up for and nothing to leak, because nothing leaves the house."
  },
  {
    title: "Runs on the tablet you already have",
    body:
      "Android 8.0 and up. The daemon bundles its own Java runtime, so the PC side is unzip-and-run with nothing to install first."
  }
];

export default function HomePage() {
  return (
    <>
      <section className="mx-auto max-w-[1264px] px-5 pt-12 pb-6 sm:pt-20">
        <div className="max-w-[62ch]">
          <p className="mb-3 text-xs font-semibold uppercase tracking-[.22em] text-accent/80">
            An EveDeck companion
          </p>
          <h1 className="font-display text-4xl font-bold leading-[1.1] tracking-tight text-text sm:text-5xl">
            Your intel channels, on the tablet next to your keyboard.
          </h1>
          <p className="mt-5 text-lg leading-relaxed text-muted">
            EveDeck Intel reads the chat logs EVE Online already writes to your disk, works out what
            the intel channels are actually saying, and puts it on a second screen you can glance at.
            No account, no API keys, nothing leaves your network.
          </p>

          <div className="mt-8 flex flex-wrap items-center gap-3">
            <Link
              href="/download"
              className="rounded-xl border border-accent/30 bg-accent/15 px-5 py-3 font-display text-sm font-semibold tracking-tight text-text no-underline transition-colors hover:border-accent/60 hover:bg-accent/25"
            >
              Download {VERSION}
            </Link>
            <Link
              href="/setup"
              className="rounded-xl border border-white/10 px-5 py-3 font-display text-sm font-semibold tracking-tight text-muted no-underline transition-colors hover:border-white/25 hover:text-text"
            >
              How to set it up
            </Link>
          </div>

          <p className="mt-4 text-sm text-muted/70">
            Windows 10/11 and an Android 8.0 tablet. Both halves are needed — they find each other
            over your LAN.
          </p>
        </div>
      </section>

      <section className="mx-auto max-w-[1264px] px-5 py-8">
        <FlowDiagram />
        <p className="mx-auto mt-4 max-w-[70ch] text-center text-sm leading-relaxed text-muted/80">
          The daemon never talks to the game. It reads text files EVE writes on its own, which is why
          it needs no API key, no login and no permission from anything but your own filesystem.
        </p>
      </section>

      <section className="mx-auto max-w-[1264px] px-5 py-10">
        <h2 className="font-display text-2xl font-bold tracking-tight text-text">What it gives you</h2>
        <div className="mt-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {features.map((feature) => (
            <div
              key={feature.title}
              className="rounded-2xl border border-white/6 bg-gradient-to-b from-panel/92 to-panel-2/84 p-5"
            >
              <h3 className="font-display text-base font-bold tracking-tight text-text">
                {feature.title}
              </h3>
              <p className="mt-2 text-[.92rem] leading-relaxed text-muted">{feature.body}</p>
            </div>
          ))}
        </div>
      </section>

      <section className="mx-auto max-w-[1264px] px-5 py-10">
        <div className="rounded-panel border border-white/6 bg-gradient-to-b from-panel/85 to-panel-2/80 p-6 sm:p-8">
          <h2 className="font-display text-2xl font-bold tracking-tight text-text">
            How it reads a channel
          </h2>
          <p className="mt-4 max-w-[70ch] leading-relaxed text-muted">
            An intel channel is a stream of system names, pilot names and hull types typed by people
            in a hurry, with two or more spaces separating one thing from the next. The parser works
            through a line segment by segment, matching each as a whole before it looks inside.
          </p>
          <p className="mt-3 max-w-[70ch] leading-relaxed text-muted">
            People abbreviate, so the daemon needs to know which regions the channel covers —
            otherwise a three-character stub has no unambiguous answer. That is the one setting most
            worth getting right, and it is a checklist in the settings window, not a config file you
            have to learn.
          </p>
          <p className="mt-3 max-w-[70ch] leading-relaxed text-muted">
            Your character&apos;s position comes from the same place as everything else: the Local
            channel records a line every time a character changes system. No ESI, no SSO, no keys.
          </p>
        </div>
      </section>

      <section className="mx-auto max-w-[1264px] px-5 py-10 pb-16">
        <div className="grid gap-4 sm:grid-cols-2">
          {DOWNLOADS.map((item) => (
            <Link
              key={item.id}
              href="/download"
              className="group flex flex-col rounded-2xl border border-white/6 bg-gradient-to-b from-panel/92 to-panel-2/84 p-5 no-underline transition-colors hover:border-accent/25"
            >
              <div className="flex items-baseline justify-between gap-3">
                <span className="text-xs font-semibold uppercase tracking-[.18em] text-muted/70">
                  {item.platform}
                </span>
                <span className="text-xs text-muted/70">{item.size}</span>
              </div>
              <h3 className="mt-2 font-display text-base font-bold tracking-tight text-text group-hover:text-accent">
                {item.title}
              </h3>
              <p className="mt-2 flex-1 text-[.92rem] leading-relaxed text-muted">{item.detail}</p>
              <p className="mt-3 text-xs text-muted/70">{item.requirement}</p>
            </Link>
          ))}
        </div>

        <p className="mt-6 text-sm leading-relaxed text-muted/80">
          EveDeck Intel is free and open source, built alongside{" "}
          <a
            href="https://evedeck.space"
            className="border-b border-accent/30 text-accent no-underline hover:border-accent/60"
          >
            EveDeck
          </a>
          . The source is in the{" "}
          <a
            href={SOURCE_URL}
            className="border-b border-accent/30 text-accent no-underline hover:border-accent/60"
          >
            EveDeck repository
          </a>{" "}
          under <code className="rounded bg-accent/10 px-1.5 py-0.5 text-accent">intel/</code>.
        </p>
      </section>
    </>
  );
}
