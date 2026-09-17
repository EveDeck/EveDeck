import type { Metadata } from "next";
import { Space_Grotesk } from "next/font/google";
import "./globals.css";
import { SiteHeader } from "@/components/site-header";
import { SiteFooter } from "@/components/site-footer";

const spaceGrotesk = Space_Grotesk({
  subsets: ["latin"],
  weight: ["500", "600", "700"],
  variable: "--font-space-grotesk",
  display: "swap"
});

const siteDescription =
  "EveDeck Intel mirrors your EVE Online alliance intel channels onto an Android tablet over your own network — live feed, jump distance, region map and hostile alerts. No account, no API keys, nothing leaves your network.";

export const metadata: Metadata = {
  metadataBase: new URL(process.env.NEXT_PUBLIC_SITE_URL || "https://intel.evedeck.space"),
  title: {
    default: "EveDeck Intel",
    template: "EveDeck Intel - %s"
  },
  description: siteDescription,
  applicationName: "EveDeck Intel",
  keywords: [
    "EVE Online",
    "intel channel",
    "intel tool",
    "EveDeck Intel",
    "nullsec",
    "tablet",
    "Android",
    "jump distance"
  ],
  // No canonical here on purpose: root metadata is inherited by every route, so a value of "/"
  // would canonicalize the whole site onto the homepage.
  openGraph: {
    type: "website",
    siteName: "EveDeck Intel",
    title: "EveDeck Intel — your intel channels, on the tablet next to your keyboard.",
    description: siteDescription,
    locale: "en_US"
  },
  twitter: {
    card: "summary",
    title: "EveDeck Intel — your intel channels, on the tablet next to your keyboard.",
    description: siteDescription
  }
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en" className={spaceGrotesk.variable}>
      <body className="min-h-screen flex flex-col bg-bg-0 text-text font-body antialiased selection:bg-accent/25 selection:text-white">
        <div
          aria-hidden="true"
          className="fixed inset-0 z-0 bg-cover bg-center bg-fixed bg-no-repeat"
          style={{
            backgroundImage: [
              "radial-gradient(1200px 700px at 20% 10%, rgba(34,195,255,.08), transparent 55%)",
              "radial-gradient(900px 600px at 80% 0%, rgba(155,125,255,.08), transparent 60%)",
              "linear-gradient(180deg, rgba(7,10,15,.82), rgba(11,15,20,.90))",
              "url('/images/site-bg-spaceship-v2.png')"
            ].join(", ")
          }}
        />
        <SiteHeader />
        <main className="relative z-10 flex-1 w-full">{children}</main>
        <SiteFooter />
      </body>
    </html>
  );
}
