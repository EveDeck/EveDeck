/**
 * The published release, in one place.
 *
 * Three pages quote the version, the file names and the sizes, and a release that updates two of
 * them is worse than one that updates none -- a download page disagreeing with itself is the kind
 * of thing that makes people assume the binary is wrong too.
 */
export const RELEASE_TAG = "intel-v0.1.0";
export const VERSION = "0.1.0";

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
    sha256: "a281ee99c8ec45a46cd59d5ffb1811deff6841dc5b98d5889419dddc4ce60b49",
    requirement: "Windows 10 or 11, 64-bit",
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
    sha256: "ae270a422f33b5abd1848f797e7d0c8e1442953e9430c9e06a774e2384094db9",
    requirement: "Android 8.0 or newer, tablet",
    detail:
      "The feed, the map and the alerts. Sideloaded, so Android asks once whether to allow installs from your browser."
  }
];
