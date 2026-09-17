"use client";

import Image from "next/image";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useMemo, useState } from "react";

const navItems = [
  { href: "/", label: "Overview" },
  { href: "/download", label: "Download" },
  { href: "/setup", label: "Setup" }
] as const;

const subtitleMap: Record<string, string> = {
  "/": "Intel",
  "/download": "Download",
  "/setup": "Setup",
  "/privacy": "Privacy Policy",
  "/legal-notice": "Legal Notice"
};

function normalizePath(pathname: string): string {
  if (pathname.endsWith("/") && pathname.length > 1) {
    return pathname.slice(0, -1);
  }
  return pathname;
}

export function SiteHeader() {
  const pathname = usePathname();
  const [open, setOpen] = useState(false);

  const currentPath = useMemo(() => normalizePath(pathname || "/"), [pathname]);
  const subtitle = subtitleMap[currentPath] ?? "Intel";

  return (
    <header className="sticky top-0 z-40 border-b border-white/6 bg-bg-0/85 backdrop-blur-md">
      <div className="mx-auto flex h-[74px] max-w-[1264px] items-center gap-4 px-5">
        <Link
          className="flex min-w-0 items-center gap-3 text-text no-underline"
          href="/"
          title="EveDeck Intel"
          onClick={() => setOpen(false)}
        >
          <Image
            className="rounded-xl shadow-[0_0_0_1px_rgba(160,220,255,.16),0_10px_30px_rgba(0,0,0,.35)]"
            src="/images/evedeck-mark-v3.png"
            alt="EveDeck Intel"
            width={44}
            height={44}
          />
          <span className="flex min-w-0 flex-col leading-tight">
            <span className="truncate font-display text-sm font-bold uppercase tracking-[.06em]">
              EveDeck
            </span>
            <span className="truncate text-[.85rem] text-muted">{subtitle}</span>
          </span>
        </Link>

        <button
          className="ml-auto grid h-[46px] w-[46px] place-items-center rounded-[14px] border border-white/6 bg-panel/55 sm:hidden"
          type="button"
          aria-label="Toggle navigation"
          aria-expanded={open}
          onClick={() => setOpen((value) => !value)}
        >
          <span className="relative block h-[2px] w-5 bg-text/75 before:absolute before:-top-[6px] before:block before:h-[2px] before:w-5 before:bg-text/75 before:content-[''] after:absolute after:top-[6px] after:block after:h-[2px] after:w-5 after:bg-text/75 after:content-['']" />
        </button>

        <nav
          className={`${
            open ? "flex" : "hidden"
          } absolute inset-x-0 top-[74px] flex-col gap-1 border-b border-white/6 bg-bg-0/95 p-4 sm:static sm:ml-auto sm:flex sm:flex-row sm:border-none sm:bg-transparent sm:p-0`}
          aria-label="Site navigation"
        >
          {navItems.map((item) => {
            const active = currentPath === item.href;
            return (
              <Link
                key={item.href}
                href={item.href}
                onClick={() => setOpen(false)}
                aria-current={active ? "page" : undefined}
                className={`rounded-xl px-3 py-2 text-[.95rem] no-underline transition-colors ${
                  active
                    ? "border border-accent/25 bg-accent/10 text-text"
                    : "border border-transparent text-muted hover:text-text"
                }`}
              >
                {item.label}
              </Link>
            );
          })}
          {/* The main site is the parent product, not a peer: an external link, spelled out. */}
          <a
            href="https://evedeck.space"
            onClick={() => setOpen(false)}
            className="rounded-xl border border-transparent px-3 py-2 text-[.95rem] text-muted no-underline transition-colors hover:text-text"
          >
            EveDeck &#8599;
          </a>
        </nav>
      </div>
    </header>
  );
}
