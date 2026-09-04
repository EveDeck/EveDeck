# Third-Party Notices

EveDeck is an independent project. This file records other EVE Online window-manager tools
whose source has been used as a reference or adapted here, and their licences.

**Working rule:** reimplementing an *idea* or *behaviour* seen in another tool needs nothing but
a mention here. Copying or closely adapting *code* additionally needs (a) a one-line pointer in
the file header of the EveDeck source that received it (`// Adapted from <project> (<file>), MIT
- see THIRD-PARTY-NOTICES.md`) and (b) that project's copyright line kept in the section below.
All three tools below hold the same EVE EULA line EveDeck does (passive window management +
thumbnails, no input broadcasting), which is why they are the reference set.

---

## EVE-O Preview

- Source: https://github.com/Phrynohyas/eve-o-preview (C#/.NET; active forks continue it)
- Licence: MIT
- Copyright (c) 2010-2016 StinkRay, Makari Aeron, CCP FoxFour, Anton V/ Kasyanov
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
