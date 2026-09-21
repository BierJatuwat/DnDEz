import { create } from "zustand";
import { persist, createJSONStorage } from "zustand/middleware";
import { apiFetch, setUnauthorizedHandler } from "./api";

type AuthState = {
  token: string | null;
  userId: string | null;
  /** false จนกว่า zustand persist จะอ่านค่าจาก localStorage เสร็จ (กัน SSR/CSR mismatch
   *  และกัน redirect ไป /login แว้บนึงก่อนรู้ว่ามี token อยู่แล้ว) */
  hasHydrated: boolean;
  setAuth: (token: string, userId: string) => void;
  logout: () => void;
  _setHasHydrated: (value: boolean) => void;
};

export const useAuthStore = create<AuthState>()(
  persist(
    (set) => ({
      token: null,
      userId: null,
      hasHydrated: false,
      setAuth: (token, userId) => set({ token, userId }),
      logout: () => set({ token: null, userId: null }),
      _setHasHydrated: (value) => set({ hasHydrated: value }),
    }),
    {
      name: "dndez-auth",
      storage: createJSONStorage(() => localStorage),
      partialize: (state) => ({ token: state.token, userId: state.userId }),
      onRehydrateStorage: () => (state) => {
        state?._setHasHydrated(true);
      },
    },
  ),
);

/**
 * token หมดอายุ (1 ชั่วโมง ยังไม่มี refresh token) = จบ session ทันที
 *
 * หน้าแรกกับ layout ของแคมเปญ redirect ไป `/login` เองอยู่แล้วเมื่อ `token` เป็น null จึงไม่ต้องสั่ง
 * navigate จากตรงนี้ · ถ้าไม่ทำ หน้าจอจะยังคิดว่าล็อกอินอยู่แล้วแสดง "ยังไม่ได้อยู่โต๊ะไหนเลย"
 * ทั้งที่แคมเปญยังอยู่ครบ — ผู้ใช้เจอจริงแล้วเข้าใจว่าแคมเปญหาย (ดู doc comment ใน `api.ts`)
 */
setUnauthorizedHandler(() => {
  const { token, logout } = useAuthStore.getState();
  if (token) logout();
});

export async function login(email: string, password: string) {
  const result = await apiFetch<{ token: string; userId: string }>(
    "/auth/login",
    { method: "POST", body: { email, password } },
  );
  useAuthStore.getState().setAuth(result.token, result.userId);
}

export async function register(
  email: string,
  password: string,
  displayName?: string,
) {
  const result = await apiFetch<{ token: string; userId: string }>(
    "/auth/register",
    { method: "POST", body: { email, password, displayName } },
  );
  useAuthStore.getState().setAuth(result.token, result.userId);
}
