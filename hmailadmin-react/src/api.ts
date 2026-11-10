export type Stats = { sessions: number[]; queue: number; messages: { total: number; spam: number; virus: number } }
export type Domain = { id: number; name: string; active: boolean; accounts: number; quotaMb: number; usedMb: number }
export type Account = { id: number; address: string; active: boolean; quotaMb: number; usedMb: number; lastLogon: string }
export type QueueItem = { id: number; from: string; to: string; queuedAt: string; retries: number }

const BASE = import.meta.env.VITE_API_URL ?? '/api'
const MOCK = import.meta.env.VITE_MOCK !== 'false' // set VITE_MOCK=false to use the real bridge

export const auth = {
  get token() { return sessionStorage.getItem('hm_token') },
  set(t: string | null) { t ? sessionStorage.setItem('hm_token', t) : sessionStorage.removeItem('hm_token') },
}

async function http<T>(path: string, init: RequestInit = {}): Promise<T> {
  const res = await fetch(BASE + path, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...(auth.token ? { Authorization: `Bearer ${auth.token}` } : {}) },
  })
  if (res.status === 401) { auth.set(null); location.assign('/login') }
  if (!res.ok) throw new Error((await res.text()) || res.statusText)
  return res.status === 204 ? (undefined as T) : res.json()
}

const mock = {
  domains: [
    { id: 1, name: 'adsun.vn', active: true, accounts: 24, quotaMb: 20480, usedMb: 15210 },
    { id: 2, name: 'melia-resort.vn', active: true, accounts: 9, quotaMb: 8192, usedMb: 1630 },
    { id: 3, name: 'adgps.vn', active: false, accounts: 3, quotaMb: 2048, usedMb: 1990 },
  ] as Domain[],
  accounts: [
    { id: 1, address: 'kiem@adsun.vn', active: true, quotaMb: 2048, usedMb: 612, lastLogon: '2026-11-10 08:41' },
    { id: 2, address: 'booking@melia-resort.vn', active: true, quotaMb: 1024, usedMb: 944, lastLogon: '2026-11-09 22:05' },
    { id: 3, address: 'support@adgps.vn', active: false, quotaMb: 512, usedMb: 120, lastLogon: '2026-10-02 14:17' },
  ] as Account[],
  queue: [
    { id: 101, from: 'booking@melia-resort.vn', to: 'guest@example.com', queuedAt: '2026-11-10 09:12', retries: 2 },
    { id: 102, from: 'kiem@adsun.vn', to: 'partner@example.org', queuedAt: '2026-11-10 09:30', retries: 0 },
  ] as QueueItem[],
}
const wait = <T,>(v: T) => new Promise<T>(r => setTimeout(() => r(structuredClone(v)), 250))

export const api = {
  login: (username: string, password: string) =>
    MOCK ? wait({ token: 'mock' }) : http<{ token: string }>('/auth/login', { method: 'POST', body: JSON.stringify({ username, password }) }),
  stats: () =>
    MOCK ? wait<Stats>({ sessions: [4, 6, 5, 9, 12, 8, 14, 11, 16, 13, 18, 15], queue: mock.queue.length, messages: { total: 12840, spam: 3120, virus: 14 } })
         : http<Stats>('/stats'),
  domains: () => (MOCK ? wait(mock.domains) : http<Domain[]>('/domains')),
  accounts: () => (MOCK ? wait(mock.accounts) : http<Account[]>('/accounts')),
  queue: () => (MOCK ? wait(mock.queue) : http<QueueItem[]>('/queue')),
  deleteQueueItem: (id: number) => {
    if (MOCK) { mock.queue = mock.queue.filter(q => q.id !== id); return wait(undefined) }
    return http<void>(`/queue/${id}`, { method: 'DELETE' })
  },
}
