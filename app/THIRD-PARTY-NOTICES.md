# Third-Party Notices

EveDeck is an independent project. This file records other EVE Online window-manager tools
whose source has been used as a reference or adapted here, and their licences.

**Working rule:** reimplementing an *idea* or *behaviour* seen in another tool needs nothing but
a mention here — ideas are not copyrightable. Copying or closely adapting *code* additionally
needs (a) a one-line pointer in the file header of the EveDeck source that received it
(`// Adapted from <project> (<file>), <licence> - see THIRD-PARTY-NOTICES.md`) and (b) that
project's copyright line kept in the section below.

Licensing context: EveDeck is GPL-3.0. All sources below are MIT, so their code can be adapted
into EveDeck freely with attribution; MIT-into-GPL is fine. The repo is private for now and may
go public later — either way the attribution obligation is the same.

`EveOPlus/eve-o-preview` (the GPL-3.0 "EVE-O Plus" fork) is **deliberately NOT a source** — it
carries features the project owner considers outside EveDeck's EULA line (its
`Eve-O-Preview.Robin/DXHook.cs` does Direct3D present-chain hooking). EveDeck's `COMPLIANCE.md`
/ `AGENTS.md` currently forbid hooking/injecting the EVE client; none of the sources below need
it, so parity with them does not depend on lifting that ban.

---

## EVE-O Preview (Proopai/eve-o-preview)

- Source: https://github.com/Proopai/eve-o-preview (C#/.NET; the compliant maintained fork —
  README limits it to foreground/resize/minimize of the EVE window, no hooking)
- Licence: MIT
- Copyright (c) 2010-2016 StinkRay, Makari Aeron, CCP FoxFour, Anton V/ Kasyanov
- Used here: design reference only so far (no code adapted). C#, so code can be adapted directly.

## EVE-APM Preview

- Source: https://github.com/mrmjstc/eve-apm-preview (C++/Qt)
- Licence: MIT for the application code; Qt is under LGPL v3 (not vendored by EveDeck).
- Copyright (c) 2025 mrmjstc
- Used here: design reference only so far (no code adapted). Because it is C++/Qt, anything
  taken from it is a reimplementation in C#/WPF rather than a copy.

## EVE-X Preview

- Source: https://github.com/g0nzo83/EVE-X-Preview
- Licence: MIT
- Copyright (c) 2024 g0nzo83
- Written in **AutoHotkey**, so there is no code to lift into a C#/WPF app — it is an
  idea/behaviour reference only.

---

## MIT License (applies to all three projects above)

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
