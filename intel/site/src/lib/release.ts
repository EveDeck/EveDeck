/**
 * The published release, in one place.
 *
 * Three pages quote the version, the file names and the sizes, and a release that updates two of
 * them is worse than one that updates none -- a download page disagreeing with itself is the kind
 * of thing that makes people assume the binary is wrong too.
 */
export const RELEASE_TAG = "intel-v0.1.2";
export const VERSION = "0.1.2";

const BASE = `https://github.com/EveDeck/EveDeck/releases/download/${RELEASE_TAG}`;

export const RELEASE_PAGE = `https://github.com/EveDeck/EveDeck/releases/tag/${RELEASE_TAG}`;
export const SOURCE_URL = "https://github.com/EveDeck/EveDeck/tree/main/intel";

export type Download = {
  id: string;
  platform: string;
  title: string;
  file: string;
  href: string;
  size: string;
  sha256: string;
  requirement: string;
  detail: string;
};

export const DOWNLOADS: Download[] = [
  {
    id: "daemon",
    platform: "Windows",
    title: "Intel daemon",
    file: `EveDeckIntel-daemon-${VERSION}-win-x64.zip`,
    href: `${BASE}/EveDeckIntel-daemon-${VERSION}-win-x64.zip`,
    size: "40 MB",
    sha256: "ca6ce6bd37f057c45f82fa21276871f35ffb591701858ff327d8a9e97f3ff091",
    requirement: "Windows 10 or 11, 64-bit — ~150 MB RAM, near-idle CPU while running",
    detail:
      "Reads the chat logs and serves them to the tablet. Unzip and run — Java is bundled, so nothing has to be installed first."
  },
  {
    id: "apk",
    platform: "Android",
    title: "Tablet app",
    file: `EveDeckIntel-${VERSION}.apk`,
    href: `${BASE}/EveDeckIntel-${VERSION}.apk`,
    size: "15 MB",
    sha256: "8ab6c0cf8889ab5a82e29ac5c729573a62478b4df84b47cb1a6ed2760cd2fd69",
    requirement: "Android 8.0 or newer, tablet — ~170 MB RAM",
    detail:
      "The feed, the map and the alerts. Sideloaded, so Android asks once whether to allow installs from your browser."
  }
];
