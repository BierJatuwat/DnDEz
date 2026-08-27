"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { useAuthStore, login, register } from "@/lib/auth-store";
import { ApiError } from "@/lib/api";

export default function LoginPage() {
  const router = useRouter();
  const token = useAuthStore((s) => s.token);
  const ready = useAuthStore((s) => s.hasHydrated);
  const [mode, setMode] = useState<"login" | "register">("login");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    if (ready && token) {
      router.replace("/");
    }
  }, [ready, token, router]);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setSubmitting(true);
    try {
      if (mode === "login") {
        await login(email, password);
      } else {
        await register(email, password, displayName || undefined);
      }
      router.replace("/");
    } catch (err) {
      if (err instanceof ApiError) {
        const message =
          (err.body as { error?: string } | null)?.error ?? `HTTP ${err.status}`;
        setError(message);
      } else {
        setError("เชื่อมต่อ API ไม่ได้ — เช็คว่า backend รันอยู่ไหม");
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main className="flex flex-1 items-center justify-center p-6">
      <form
        onSubmit={handleSubmit}
        className="w-full max-w-sm space-y-4 rounded-lg border border-black/10 dark:border-white/10 p-6"
      >
        <h1 className="text-xl font-semibold">
          {mode === "login" ? "เข้าสู่ระบบ" : "สมัครสมาชิก"}
        </h1>

        <label className="block space-y-1">
          <span className="text-sm">Email</span>
          <input
            type="email"
            required
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            className="w-full rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-2"
          />
        </label>

        <label className="block space-y-1">
          <span className="text-sm">Password</span>
          <input
            type="password"
            required
            minLength={8}
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            className="w-full rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-2"
          />
        </label>

        {mode === "register" && (
          <label className="block space-y-1">
            <span className="text-sm">ชื่อที่แสดง (ไม่บังคับ)</span>
            <input
              type="text"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
              className="w-full rounded border border-black/20 dark:border-white/20 bg-transparent px-3 py-2"
            />
          </label>
        )}

        {error && <p className="text-sm text-red-500">{error}</p>}

        <button
          type="submit"
          disabled={submitting}
          className="w-full rounded bg-indigo-600 px-4 py-2 text-white disabled:opacity-50"
        >
          {submitting
            ? "กำลังทำงาน..."
            : mode === "login"
              ? "เข้าสู่ระบบ"
              : "สมัครสมาชิก"}
        </button>

        <button
          type="button"
          onClick={() => setMode(mode === "login" ? "register" : "login")}
          className="w-full text-sm text-indigo-500 underline"
        >
          {mode === "login" ? "ยังไม่มีบัญชี? สมัครสมาชิก" : "มีบัญชีแล้ว? เข้าสู่ระบบ"}
        </button>
      </form>
    </main>
  );
}
