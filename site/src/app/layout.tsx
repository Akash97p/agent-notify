import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";

import { SiteFooter } from "@/components/site-footer";
import { SiteHeader } from "@/components/site-header";
import { basePath, site } from "@/lib/site";

import "./globals.css";

const geistSans = Geist({ variable: "--font-geist-sans", subsets: ["latin"] });
const geistMono = Geist_Mono({ variable: "--font-geist-mono", subsets: ["latin"] });

export const metadata: Metadata = {
  metadataBase: new URL(site.url),
  title: { default: "AgentNotify — human attention for coding agents", template: "%s · AgentNotify" },
  description: site.description,
  manifest: `${basePath}/favicon/site.webmanifest`,
  icons: {
    icon: [
      { url: `${basePath}/favicon/favicon.ico` },
      { url: `${basePath}/favicon/favicon-32x32.png`, sizes: "32x32", type: "image/png" },
    ],
    apple: `${basePath}/favicon/apple-touch-icon.png`,
  },
  openGraph: {
    type: "website",
    url: site.url,
    title: "AgentNotify",
    description: site.description,
    images: [`${site.url}/an.png`],
  },
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en" className="dark">
      <body className={`${geistSans.variable} ${geistMono.variable} min-h-screen antialiased`}>
        <SiteHeader />
        {children}
        <SiteFooter />
      </body>
    </html>
  );
}
