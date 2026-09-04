# Third-Party Notices

EveDeck is an independent project. This file records other EVE Online window-manager tools
whose source has been used as a reference or adapted here, and their licences.

**Working rule:** reimplementing an *idea* or *behaviour* seen in another tool needs nothing but
a mention here — ideas are not copyrightable. Copying or closely adapting *code* additionally
needs (a) a one-line pointer in the file header of the EveDeck source that received it
(`// Adapted from <project> (<file>), <licence> - see THIRD-PARTY-NOTICES.md`) and (b) that
project's copyright line kept in the section below — **and, for a GPL source, see the licence
warning under EVE-O Plus.**

The tools below hold roughly the same EVE EULA line EveDeck does (passive window management +
thumbnails, no input broadcasting). **Exception:** EVE-O Plus's `Eve-O-Preview.Robin` component
contains `DXHook.cs` (Direct3D present-chain hooking). EveDeck's own `COMPLIANCE.md` / `AGENTS.md`
forbid hooking or injecting into the EVE client, and EveDeck deliberately declined FPS limiting
for exactly this reason. Do not port that piece from any of these tools regardless of licence.

---

## EVE-O Preview (upstream)

- Source: https://github.com/Phrynohyas/eve-o-preview (C#/.NET; WinForms)
- Licence: MIT
- Copyright (c) 2010-2016 StinkRay, Makari Aeron, CCP FoxFour, Anton V/ Kasyanov
- Used here: design reference only so far (no code adapted).

## EVE-O Plus (EveOPlus/eve-o-preview) — active fork

- Source: https://github.com/EveOPlus/eve-o-preview (C#/.NET; the maintained fork)
- Licence: **GPL-3.0** as of commit `b8b25d9` (2026); commits *before* `b8b25d9` are still
  available under MIT.
- Copyright (c) 2026 Aura Asuna (post-relicense); MIT lineage before that.
- **Licence warning:** copying/adapting any post-`b8b25d9` code makes a *distributed* EveDeck
  subject to GPL-3.0 in its entirety (full corresponding source must be offered). This is far
  more than attribution. To keep EveDeck's licensing open, take **ideas** from EVE-O Plus and
  copy **code** only from an MIT source (upstream EVE-O Preview, a pre-`b8b25d9` EVE-O Plus
  commit, or EVE-APM Preview). EveDeck's repo is currently private, so nothing is triggered
  until a build is shared.
- Used here: design reference only so far (no code adapted).

## EVE-APM Preview

- Source: https://github.com/mrmjstc/eve-apm-preview (C++/Qt)
- Licence: MIT for the application code; Qt is under LGPL v3 (not vendored by EveDeck).
- Copyright (c) 2025 mrmjstc
- Used here: design reference only so far (no code adapted). Because it is C++/Qt, anything
  taken from it is a reimplementation in C#/WPF rather than a copy.

## EVE-X Preview

- A fork line of EVE-O Preview (EVE forums thread); MIT lineage from the EVE-O base.
- Verify the specific fork repo's own LICENSE before adapting any of its *own* additions.
- Used here: design reference only so far (no code adapted).

---

## MIT License (applies to the projects above unless noted)

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    SOFTWARE.
