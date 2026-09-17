import type { Metadata } from "next";
import Link from "next/link";
import { PageShell } from "@/components/page-shell";
import { DOWNLOADS, RELEASE_PAGE, SOURCE_URL, VERSION } from "@/lib/release";

export const metadata: Metadata = {
  title: "Download",
  description:
    "Download EveDeck Intel: the Windows daemon and the Android tablet app, with checksums. Both halves are needed."
};

export default function DownloadPage() {
  return (
    <PageShell
      title={`Download ${VERSION}`}
      kicker="BOTH HALVES"
      intro="The daemon runs on the PC you play on. The app runs on the tablet. They find each other over your LAN, so you need both."
    >
      <div className="not-prose mt-2 grid gap-4 sm:grid-cols-2">
        {DOWNLOADS.map((item) => (
          <a
            key={item.id}
            href={item.href}
            className="group flex flex-col overflow-hidden rounded-2xl border border-white/6 bg-gradient-to-b from-panel/92 to-panel-2/84 p-5 no-underline transition-colors hover:border-accent/25"
          >
            <div className="flex items-baseline justify-between gap-3">
              <span className="text-xs font-semibold uppercase tracking-[.18em] text-muted/70">
                {item.platform}
              </span>
              <span className="text-xs text-muted/70">{item.size}</span>
            </div>
            <h2 className="mt-2 font-display text-lg font-bold tracking-tight text-text group-hover:text-accent">
              {item.title}
            </h2>
            <p className="mt-2 flex-1 text-[.92rem] leading-relaxed text-muted">{item.detail}</p>
            <p className="mt-3 text-xs text-muted/70">{item.requirement}</p>
            <p className="mt-3 break-all font-mono text-[.66rem] leading-relaxed text-muted/50">
              {item.file}
              <br />
              sha256 {item.sha256}
            </p>
          </a>
        ))}
      </div>

      <p className="mt-5 text-sm text-muted/80">
        Both files are also on the{" "}
        <a href={RELEASE_PAGE}>{VERSION} release page</a>, which is where the checksums above come
        from. Next step: <Link href="/setup">setting it up</Link>.
      </p>

      <h2>Verifying what you downloaded</h2>
      <p>
        You do not have to, but it costs one line. In PowerShell, run{" "}
        <code>Get-FileHash .\EveDeckIntel-daemon-{VERSION}-win-x64.zip</code> and compare the result
        with the checksum on the card above. If they differ, the file is not the one we published —
        delete it.
      </p>

      <h2>What the warnings mean</h2>
      <p>
        Neither download carries a certificate from a certificate authority, because a code-signing
        certificate costs more per year than this project costs to run. Two consequences, and both
        are cosmetic rather than a sign of anything wrong:
      </p>
      <ul>
        <li>
          <strong>Windows SmartScreen</strong> will interrupt the first launch of the daemon with a
          blue &ldquo;unrecognised app&rdquo; panel. Choose <em>More info</em>, then{" "}
          <em>Run anyway</em>. It stops once the file has enough reputation.
        </li>
        <li>
          <strong>Android</strong> treats any sideloaded app as unknown and asks whether to allow
          installs from whatever app you downloaded it with. The APK <em>is</em> signed, with a key
          we hold — that signature is what lets a later version upgrade an installed one rather than
          forcing a reinstall.
        </li>
      </ul>
      <p>
        If you would rather not trust a binary at all, the whole thing builds from source: the
        repository is <a href={SOURCE_URL}>public</a>, and the daemon is a Gradle project like any
        other.
      </p>

      <h2>Updating</h2>
      <p>
        Neither half updates itself. The daemon is a folder — unzip a newer one over it and keep your{" "}
        <code>eveintel.properties</code>. The APK installs over the previous version.
      </p>

      <hr />
      <p className="text-sm">
        EveDeck Intel is free and open source, released under the same licence as EveDeck itself.
        EVE Online and all related material are the property of Fenris Creations hf. This is
        independent work and is not endorsed by or affiliated with them.
      </p>
    </PageShell>
  );
}
