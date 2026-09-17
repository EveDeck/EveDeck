import type { Metadata } from "next";
import { PageShell } from "@/components/page-shell";

export const metadata: Metadata = {
  title: "Legal Notice",
  description:
    "Licence, bundled component licensing, and the Fenris Creations proprietary notice for EveDeck Intel."
};

export default function LegalNoticePage() {
  return (
    <PageShell title="Legal Notice" kicker="LICENSE">
      <h2>License (GPL-3.0)</h2>
      <p>
        EveDeck Intel is part of the EveDeck project and is free software: you can redistribute it
        and/or modify it under the terms of the{" "}
        <a
          href="https://github.com/EveDeck/EveDeck/blob/main/LICENSE"
          target="_blank"
          rel="noopener noreferrer"
        >
          GNU General Public License v3.0
        </a>{" "}
        as published by the Free Software Foundation.
      </p>
      <p>Copyright (c) 2026 EveDeck contributors</p>
      <p>
        It is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even
        the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
        General Public License for more details.
      </p>
      <p>
        The GPL-3.0 covers the source code only. It does not grant any rights to the EveDeck name,
        logo, or the evedeck.space domain, which remain the exclusive property of the EveDeck project
        — see the{" "}
        <a
          href="https://github.com/EveDeck/EveDeck/blob/main/TRADEMARKS.md"
          target="_blank"
          rel="noopener noreferrer"
        >
          trademark notice
        </a>{" "}
        for what this means if you fork the project.
      </p>

      <h2>Independent work</h2>
      <p>
        EveDeck Intel contains no code from RIFT or any other third-party intel tool. The chat log
        format was determined by inspecting log files on disk, and the parser, the universe graph and
        both applications are original work.
      </p>

      <h2>Dependencies and bundled components</h2>
      <p>
        The daemon bundles a trimmed OpenJDK runtime (GPL-2.0 with Classpath Exception) and uses
        Kotlin, Ktor and FlatLaf, all under the Apache License 2.0. The tablet app uses AndroidX and
        Jetpack Compose (Apache 2.0) and Coil (Apache 2.0). The universe data is built from{" "}
        <a href="https://developers.eveonline.com/" target="_blank" rel="noopener noreferrer">
          CCP&apos;s published Static Data Export
        </a>
        . Icons and artwork are original work by the project.
      </p>

      <hr />

      <h2>Fenris Creations Proprietary Notice</h2>
      <p>
        Copyright (c) 2003-2026 Fenris Creations hf. All rights reserved. EVE, EVE Online, Fenris
        Creations, and all related logos and images are trademarks or registered trademarks of Fenris
        Creations hf.
      </p>
      <p>
        Developer License Agreement:{" "}
        <a href="https://developers.eveonline.com/license-agreement">
          developers.eveonline.com/license-agreement
        </a>
      </p>
      <p>
        EveDeck Intel is an independent project and is not affiliated with or endorsed by Fenris
        Creations.
      </p>
    </PageShell>
  );
}
