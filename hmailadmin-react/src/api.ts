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
  if (!res.ok) { const t = await res.text(); let m = t; try { m = JSON.parse(t).detail ?? t } catch { /* plain text */ } throw new Error(m || res.statusText) }
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

export type Alias = { id: number; name: string; value: string; active: boolean }
export type DomainInput = { name: string; active: boolean; quotaMb: number }
export type AccountInput = { address: string; password?: string; active: boolean; quotaMb: number }
export type AliasInput = { name: string; value: string; active: boolean }

let seq = 1000
const aliasStore: Alias[] = [{ id: 1, name: 'info@adsun.vn', value: 'kiem@adsun.vn', active: true }]
const run = (f: () => void) => { f(); return wait(undefined) }
const drop = (arr: { id: number }[], id: number) => { const i = arr.findIndex(x => x.id === id); if (i >= 0) arr.splice(i, 1) }
const send = (path: string, id: number | null, body: unknown) =>
  http<void>(id === null ? path : `${path}/${id}`, { method: id === null ? 'POST' : 'PUT', body: JSON.stringify(body) })
const remove = (path: string, id: number) => http<void>(`${path}/${id}`, { method: 'DELETE' })

export const crud = {
  saveDomain: (id: number | null, v: DomainInput) => MOCK ? run(() => {
    if (id === null) mock.domains.push({ id: ++seq, accounts: 0, usedMb: 0, ...v })
    else Object.assign(mock.domains.find(d => d.id === id)!, v)
  }) : send('/domains', id, v),
  deleteDomain: (id: number) => MOCK ? run(() => drop(mock.domains, id)) : remove('/domains', id),
  saveAccount: (id: number | null, v: AccountInput) => MOCK ? run(() => {
    const { password: _p, ...rest } = v
    if (id === null) mock.accounts.push({ id: ++seq, usedMb: 0, lastLogon: '-', ...rest })
    else Object.assign(mock.accounts.find(a => a.id === id)!, rest)
  }) : send('/accounts', id, v),
  deleteAccount: (id: number) => MOCK ? run(() => drop(mock.accounts, id)) : remove('/accounts', id),
  aliases: () => (MOCK ? wait(aliasStore) : http<Alias[]>('/aliases')),
  saveAlias: (id: number | null, v: AliasInput) => MOCK ? run(() => {
    if (id === null) aliasStore.push({ id: ++seq, ...v })
    else Object.assign(aliasStore.find(a => a.id === id)!, v)
  }) : send('/aliases', id, v),
  deleteAlias: (id: number) => MOCK ? run(() => drop(aliasStore, id)) : remove('/aliases', id),
}

export type DistList = { id: number; address: string; active: boolean; members: string[] }
export type IpRange = { id: number; name: string; lowerIp: string; upperIp: string; priority: number; allowSmtp: boolean; allowPop: boolean; allowImap: boolean; expires: string | null }
export type SecuritySettings = {
  autoBan: boolean; maxAttempts: number; withinMinutes: number; banMinutes: number
  greyEnabled: boolean; greyInitialDelayMin: number; greyInitialDeleteHours: number; greyFinalDeleteHours: number
}
export type ListInput = Omit<DistList, 'id'>
export type RangeInput = Omit<IpRange, 'id' | 'expires'>

const listStore: DistList[] = [{ id: 1, address: 'all@adsun.vn', active: true, members: ['kiem@adsun.vn', 'support@adgps.vn'] }]
const rangeStore: IpRange[] = [{ id: 1, name: 'Mạng nội bộ', lowerIp: '192.168.1.1', upperIp: '192.168.1.254', priority: 20, allowSmtp: true, allowPop: true, allowImap: true, expires: null }]
let secStore: SecuritySettings = { autoBan: true, maxAttempts: 3, withinMinutes: 30, banMinutes: 60, greyEnabled: false, greyInitialDelayMin: 1, greyInitialDeleteHours: 24, greyFinalDeleteHours: 720 }

export const ext = {
  lists: () => (MOCK ? wait(listStore) : http<DistList[]>('/lists')),
  saveList: (id: number | null, v: ListInput) => MOCK ? run(() => {
    if (id === null) listStore.push({ id: ++seq, ...v }); else Object.assign(listStore.find(x => x.id === id)!, v)
  }) : send('/lists', id, v),
  deleteList: (id: number) => MOCK ? run(() => drop(listStore, id)) : remove('/lists', id),
  ranges: () => (MOCK ? wait(rangeStore) : http<IpRange[]>('/ranges')),
  saveRange: (id: number | null, v: RangeInput) => MOCK ? run(() => {
    if (id === null) rangeStore.push({ id: ++seq, expires: null, ...v }); else Object.assign(rangeStore.find(x => x.id === id)!, v)
  }) : send('/ranges', id, v),
  deleteRange: (id: number) => MOCK ? run(() => drop(rangeStore, id)) : remove('/ranges', id),
  security: () => (MOCK ? wait(secStore) : http<SecuritySettings>('/settings/security')),
  saveSecurity: (v: SecuritySettings) => MOCK ? run(() => { secStore = v }) : http<void>('/settings/security', { method: 'PUT', body: JSON.stringify(v) }),
}
