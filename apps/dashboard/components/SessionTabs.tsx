"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useState } from "react";

const TABS = [
  { id: "poster", label: "Poster" },
  { id: "knowledge", label: "Knowledge" },
  { id: "avatar", label: "Avatar" },
  { id: "preview", label: "Preview" },
];

export default function SessionTabs({ sessionId }: { sessionId: string }) {
  const pathname = usePathname() ?? "";
  const [mounted, setMounted] = useState(false);
  useEffect(() => {
    setMounted(true);
  }, []);
  return (
    <nav className="tab-strip">
      {TABS.map((tab) => {
        const href = `/sessions/${sessionId}/${tab.id}`;
        const active = mounted && pathname.endsWith(`/${tab.id}`);
        return (
          <Link key={tab.id} href={href} className={active ? "active" : ""}>
            {tab.label}
          </Link>
        );
      })}
    </nav>
  );
}
