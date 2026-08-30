"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import { useAuthStore } from "@/lib/auth-store";
import { PlayerConsole } from "@/components/PlayerConsole";

export default function PlayPage() {
  const router = useRouter();
  const token = useAuthStore((s) => s.token);
  const ready = useAuthStore((s) => s.hasHydrated);

  useEffect(() => {
    if (ready && !token) {
      router.replace("/login");
    }
  }, [ready, token, router]);

  if (!ready || !token) {
    return (
      <main className="flex flex-1 items-center justify-center">
        <p className="opacity-50 text-sm">กำลังโหลด...</p>
      </main>
    );
  }

  return <PlayerConsole />;
}
