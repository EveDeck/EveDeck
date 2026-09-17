import type { Metadata } from "next";
import Link from "next/link";
import { PageShell } from "@/components/page-shell";
import { FlowDiagram } from "@/components/flow-diagram";

export const metadata: Metadata = {
  title: "Setup",
  description:
    "Setting up EveDeck Intel: run the daemon, pick your channels and regions, copy the tablet URL, and install the app."
};

export default function SetupPage() {
  return (
    <PageShell
      title="Setting it up"
      kicker="TEN MINUTES"
      intro="Four steps, and three of them are on the PC. Nothing here needs a config file, an API key or an account."
    >
      <div className="not-prose mb-8">
        <FlowDiagram />
      </div>

      <h2>1. Run the daemon</h2>
      <p>
        Unzip the daemon somewhere permanent. It keeps its settings beside the launcher, so a folder
        inside Downloads is a bad home for it — somewhere like{" "}
        <code>C:\Tools\EveDeck Intel</code> is fine.
      </p>
      <p>
        Run <code>EveDeck Intel.exe</code>. Windows SmartScreen will interrupt the first launch:
        choose <em>More info</em>, then <em>Run anyway</em>. The daemon settles into the system tray
        — on Windows 11 that means the overflow flyout behind the <code>^</code> at the end of the
        taskbar, and you can drag it out to keep it visible.
      </p>
      <p>
        Windows also asks once whether to allow it through the firewall.{" "}
        <strong>Allow it for private networks.</strong> Without that the tablet cannot reach it, and
        the symptom is a tablet that simply never connects.
      </p>

      <h2>2. Pick your channels and regions</h2>
      <p>
        The first run opens the settings window. Right-clicking the tray icon and choosing{" "}
        <em>Settings</em> gets you back to it any time.
      </p>
      <ul>
        <li>
          <strong>Chat logs</strong> is found for you, including the OneDrive-redirected Documents
          folder. Only change it if your logs live somewhere unusual.
        </li>
        <li>
          <strong>Intel channels</strong> lists every channel EVE has written a log for. Tick the
          ones you want read. If the list is empty or missing a channel, join it in game and wait for
          a line to appear — EVE only creates the file once there is something to put in it.
        </li>
        <li>
          <strong>Scope regions</strong> is the setting most worth getting right. People abbreviate
          system names, and a three-character stub only has one answer if the daemon knows which
          regions the channel is watching. Pick them from the list; the box above it filters.
        </li>
      </ul>
      <p>
        <code>Local</code> is always read whether or not it appears in your selection, because it is
        how the daemon knows where your characters are. Nothing is read from a channel you have not
        ticked.
      </p>
      <p>
        Press <strong>Save</strong>. Channel selection takes effect immediately; the port, the log
        folder and the regions apply when the daemon restarts, and the window tells you which of the
        two just happened.
      </p>

      <h2>3. Copy the tablet URL</h2>
      <p>
        Press <strong>Copy URL</strong> in the settings window, or use{" "}
        <em>Copy tablet URL</em> in the tray menu. It looks like{" "}
        <code>ws://192.168.1.50:31337/intel</code>.
      </p>
      <p>
        If several addresses are shown, your PC has more than one network interface — a VPN, or a
        virtual machine adapter. The one to use is the one on the same network as the tablet, which
        is almost always the <code>192.168.x.x</code> address matching your router.
      </p>

      <h2>4. Install the app</h2>
      <p>
        Download the APK on the tablet itself and open it. Android asks once whether to allow
        installs from your browser; allow it, then install. Open the app, go to its settings, paste
        the address from step 3, and choose a character to follow.
      </p>
      <p>
        The character you follow is what jump distances are measured from, so pick whichever one is
        actually out there.
      </p>

      <h2>If nothing appears</h2>
      <p>
        The tray menu has an <em>Open log</em> item, and it answers most of it. The two usual causes
        are a chat logs folder that is not the one EVE is writing to, and no channels ticked.
      </p>
      <ul>
        <li>
          <strong>The tablet will not connect.</strong> Both devices have to be on the same network,
          and the firewall rule has to exist. If you dismissed that prompt, allow{" "}
          <em>EveDeck Intel</em> for private networks in Windows Defender Firewall.
        </li>
        <li>
          <strong>Systems are resolving to the wrong place.</strong> Your scope regions are wrong or
          too broad. Narrow them to what the channel actually covers.
        </li>
        <li>
          <strong>Reports are showing up unparsed.</strong> Alliances have their own shorthand. The
          daemon can replay your own channel history and print what it failed to classify — see the{" "}
          <a href="https://github.com/EveDeck/EveDeck/tree/main/intel">repository</a> for{" "}
          <code>--validate</code>, which is how the vocabulary gets tuned.
        </li>
      </ul>

      <h2>A word on the network</h2>
      <p>
        The connection between the daemon and the tablet has no password and no encryption. That is
        deliberate for a LAN service carrying information that everyone in the channel can already
        see, and it keeps the setup down to typing one address.
      </p>
      <p>
        <strong>Do not forward its port through your router.</strong> If you want it from outside the
        house, put the PC and the tablet on the same Tailscale tailnet and use that address instead —
        same one line of setup, without exposing anything to the internet.
      </p>

      <hr />
      <p className="text-sm">
        Not downloaded it yet? Both halves are on the <Link href="/download">download page</Link>.
      </p>
    </PageShell>
  );
}
