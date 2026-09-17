import type { Metadata } from "next";
import { PageShell } from "@/components/page-shell";

export const metadata: Metadata = {
  title: "Privacy Policy",
  description:
    "How EveDeck Intel handles your data: no accounts, no telemetry, no servers. Everything stays between your PC and your tablet."
};

const LAST_UPDATED = "17 September 2026";

export default function PrivacyPage() {
  return (
    <PageShell title="Privacy Policy" kicker="PRIVACY">
      <p>
        <em>Last updated: {LAST_UPDATED}</em>
      </p>

      <h2>The short version</h2>
      <p>
        EveDeck Intel runs entirely on hardware you own. The daemon runs on your PC, the app runs on
        your tablet, and they talk to each other across your own local network. There are no
        accounts, no sign-up, and no analytics or telemetry of any kind. The EveDeck project operates
        no server that receives anything from either half.
      </p>

      <h2>What the daemon reads</h2>
      <p>
        The chat log files EVE Online writes to your own disk, and nothing else. It does not read the
        game&apos;s memory, does not attach to the client, and sends no input to it. It reads only
        the channels you tick in the settings window, plus <code>Local</code>, which is how it knows
        where your characters are.
      </p>

      <h2>What is stored, and where</h2>
      <ul>
        <li>
          <strong>Settings</strong> — an <code>eveintel.properties</code> file beside the daemon
          launcher, holding your channel selection, regions, port and log folder.
        </li>
        <li>
          <strong>A diagnostic log</strong> — <code>eveintel.log</code>, also beside the launcher,
          rewritten each time the daemon starts. It is never uploaded anywhere.
        </li>
        <li>
          <strong>A name cache</strong> — character names and portraits looked up from EVE&apos;s
          public ESI service for the pilots named in intel, cached so they are not fetched twice.
        </li>
        <li>
          <strong>On the tablet</strong> — the address you typed, the character you are following,
          and your alert settings.
        </li>
      </ul>
      <p>
        All of it is on your own devices. Deleting the daemon folder and uninstalling the app removes
        everything.
      </p>

      <h2>The one outbound connection</h2>
      <p>
        The daemon contacts EVE Online&apos;s public ESI service to turn character names into
        portraits and to check server status. Those requests carry a character or type name and
        nothing about you — no identifier, no account, no authentication, because the endpoints used
        are public and require none. If the daemon cannot reach ESI, everything else keeps working
        without portraits.
      </p>

      <h2>This website</h2>
      <p>
        This site has no analytics, no tracking scripts, no advertising and no cookies. It sets no
        cookie of any kind. Downloads are served from GitHub, so following a download link is a
        request to GitHub and subject to their privacy policy, not ours.
      </p>
      <p>
        The web server keeps standard access logs — IP address, time, page requested — for
        operational purposes, as any web server does.
      </p>

      <h2>Your network</h2>
      <p>
        The connection between the daemon and the tablet is unauthenticated and unencrypted by
        design, because it carries information already visible to everyone in the channel and lives
        on a network you control. Anyone who is already on your LAN can read it. That is the reason
        the documentation says, in several places, not to forward its port to the internet.
      </p>

      <h2>Changes</h2>
      <p>
        If this policy changes, the date at the top changes with it. Since nothing is collected,
        there is nothing here we could quietly start doing more of.
      </p>
    </PageShell>
  );
}
