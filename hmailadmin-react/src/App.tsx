import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { NavLink, Navigate, Route, Routes, useNavigate } from 'react-router-dom'
import { api, auth, type Account, type Domain, type QueueItem, type Stats } from './api'

function useLoad<T>(fn: () => Promise<T>) {
  const [data, setData] = useState<T>()
  const [error, setError] = useState('')
  const load = useCallback(() => { setError(''); fn().then(setData).catch(e => setError(e.message)) }, [])
  useEffect(load, [load])
  return { data, error, reload: load }
}

const fmtMb = (mb: number) => (mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${mb} MB`)

function Quota({ used, total }: { used: number; total: number }) {
  const pct = Math.min(100, Math.round((used / total) * 100))
  return (
    <div className="w-44">
      <div className="h-1.5 rounded-full bg-line" role="progressbar" aria-valuenow={pct} aria-valuemin={0} aria-valuemax={100}>
        <div className={`h-full rounded-full ${pct > 85 ? 'bg-warn' : 'bg-signal'}`} style={{ width: `${pct}%` }} />
      </div>
      <p className="mt-1 text-xs text-muted">{fmtMb(used)} / {fmtMb(total)}</p>
    </div>
  )
}

function Status({ active }: { active: boolean }) {
  return <span className={`text-xs font-medium ${active ? 'text-signal' : 'text-muted'}`}>{active ? 'Đang hoạt động' : 'Đã tắt'}</span>
}

function Page({ title, children, error }: { title: string; children: ReactNode; error?: string }) {
  return (
    <section className="mx-auto max-w-5xl p-4 md:p-8">
      <h1 className="mb-6 text-2xl font-semibold tracking-tight">{title}</h1>
      {error ? <p role="alert" className="rounded-lg border border-warn p-4 text-sm">Không tải được dữ liệu: {error}. Kiểm tra API rồi thử lại.</p> : children}
    </section>
  )
}

function Table({ heads, rows, empty }: { heads: string[]; rows: ReactNode[][]; empty: string }) {
  return (
    <div className="overflow-x-auto rounded-xl border border-line bg-white">
      <table className="w-full text-left text-sm">
        <thead className="border-b border-line text-muted">
          <tr>{heads.map(h => <th key={h} className="px-4 py-3 font-medium">{h}</th>)}</tr>
        </thead>
        <tbody>
          {rows.length === 0 && <tr><td colSpan={heads.length} className="px-4 py-8 text-center text-muted">{empty}</td></tr>}
          {rows.map((r, i) => (
            <tr key={i} className="border-b border-line/60 last:border-0 hover:bg-mist/60">
              {r.map((c, j) => <td key={j} className="px-4 py-3 align-middle">{c}</td>)}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function Search({ value, onChange, label }: { value: string; onChange: (v: string) => void; label: string }) {
  return <input aria-label={label} placeholder={label} value={value} onChange={e => onChange(e.target.value)}
    className="mb-4 w-full max-w-sm rounded-lg border border-line bg-white px-3 py-2 text-sm" />
}

function Spark({ data }: { data: number[] }) {
  const max = Math.max(...data, 1)
  const pts = data.map((v, i) => `${(i / (data.length - 1)) * 300},${60 - (v / max) * 54}`).join(' ')
  return <svg viewBox="0 0 300 64" className="h-16 w-full" role="img" aria-label="Phiên kết nối theo thời gian">
    <polyline points={pts} fill="none" stroke="var(--color-signal)" strokeWidth="2.5" strokeLinejoin="round" strokeLinecap="round" />
  </svg>
}

function Dashboard() {
  const { data, error } = useLoad<Stats>(api.stats)
  return (
    <Page title="Tổng quan" error={error}>
      {!data ? <p className="text-muted">Đang tải…</p> : (
        <div className="space-y-6">
          <div className="rounded-xl border border-line bg-white p-6">
            <p className="text-sm text-muted">Phiên kết nối gần đây</p>
            <Spark data={data.sessions} />
          </div>
          <dl className="grid gap-4 sm:grid-cols-4">
            {[['Thư trong hàng đợi', data.queue], ['Tổng thư', data.messages.total], ['Thư rác', data.messages.spam], ['Virus', data.messages.virus]].map(([k, v]) => (
              <div key={k} className="rounded-xl border border-line bg-white p-4">
                <dt className="text-sm text-muted">{k}</dt>
                <dd className="mt-1 text-2xl font-semibold">{Number(v).toLocaleString('vi-VN')}</dd>
              </div>
            ))}
          </dl>
        </div>
      )}
    </Page>
  )
}

function Domains() {
  const { data, error } = useLoad<Domain[]>(api.domains)
  const [q, setQ] = useState('')
  const rows = (data ?? []).filter(d => d.name.includes(q.toLowerCase()))
  return (
    <Page title="Tên miền" error={error}>
      <Search value={q} onChange={setQ} label="Tìm tên miền" />
      <Table heads={['Tên miền', 'Trạng thái', 'Tài khoản', 'Dung lượng']} empty="Chưa có tên miền nào."
        rows={rows.map(d => [<b key="n">{d.name}</b>, <Status key="s" active={d.active} />, d.accounts, <Quota key="q" used={d.usedMb} total={d.quotaMb} />])} />
    </Page>
  )
}

function Accounts() {
  const { data, error } = useLoad<Account[]>(api.accounts)
  const [q, setQ] = useState('')
  const rows = (data ?? []).filter(a => a.address.includes(q.toLowerCase()))
  return (
    <Page title="Tài khoản" error={error}>
      <Search value={q} onChange={setQ} label="Tìm tài khoản" />
      <Table heads={['Địa chỉ', 'Trạng thái', 'Đăng nhập gần nhất', 'Dung lượng']} empty="Không tìm thấy tài khoản."
        rows={rows.map(a => [<b key="n">{a.address}</b>, <Status key="s" active={a.active} />, a.lastLogon, <Quota key="q" used={a.usedMb} total={a.quotaMb} />])} />
    </Page>
  )
}

function Queue() {
  const { data, error, reload } = useLoad<QueueItem[]>(api.queue)
  const remove = async (id: number) => {
    if (!confirm(`Xoá thư #${id} khỏi hàng đợi?`)) return
    await api.deleteQueueItem(id); reload()
  }
  return (
    <Page title="Hàng đợi gửi" error={error}>
      <Table heads={['Mã', 'Từ', 'Đến', 'Vào hàng đợi', 'Thử lại', '']} empty="Hàng đợi trống."
        rows={(data ?? []).map(m => [m.id, m.from, m.to, m.queuedAt, m.retries,
          <button key="d" onClick={() => remove(m.id)} className="text-sm font-medium text-warn hover:underline">Xoá thư</button>])} />
    </Page>
  )
}

function Login() {
  const nav = useNavigate()
  const [err, setErr] = useState('')
  const submit = async (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    try { auth.set((await api.login(String(f.get('u')), String(f.get('p')))).token); nav('/') }
    catch { setErr('Sai tên đăng nhập hoặc mật khẩu.') }
  }
  return (
    <main className="grid min-h-screen place-items-center p-4">
      <form onSubmit={submit} className="w-full max-w-sm space-y-4 rounded-2xl border border-line bg-white p-8">
        <h1 className="text-2xl font-semibold">Đăng nhập hMailAdmin</h1>
        <input name="u" required autoComplete="username" placeholder="Tên đăng nhập" className="w-full rounded-lg border border-line px-3 py-2" />
        <input name="p" required type="password" autoComplete="current-password" placeholder="Mật khẩu" className="w-full rounded-lg border border-line px-3 py-2" />
        {err && <p role="alert" className="text-sm text-warn">{err}</p>}
        <button className="w-full rounded-lg bg-ink py-2.5 font-medium text-white hover:bg-signal">Đăng nhập</button>
      </form>
    </main>
  )
}

const nav = [['/', 'Tổng quan'], ['/domains', 'Tên miền'], ['/accounts', 'Tài khoản'], ['/queue', 'Hàng đợi gửi']]

function Shell() {
  const go = useNavigate()
  if (!auth.token) return <Navigate to="/login" replace />
  return (
    <div className="min-h-screen md:grid md:grid-cols-[14rem_1fr]">
      <aside className="flex flex-wrap items-center gap-1 bg-ink p-3 text-white md:flex-col md:items-stretch md:p-4">
        <p className="mr-4 px-3 py-2 text-lg font-semibold md:mb-4">hMailAdmin</p>
        {nav.map(([to, label]) => (
          <NavLink key={to} to={to} end={to === '/'}
            className={({ isActive }) => `rounded-lg px-3 py-2 text-sm ${isActive ? 'bg-signal' : 'text-white/75 hover:bg-white/10'}`}>{label}</NavLink>
        ))}
        <button onClick={() => { auth.set(null); go('/login') }} className="rounded-lg px-3 py-2 text-left text-sm text-white/75 hover:bg-white/10 md:mt-auto">Đăng xuất</button>
      </aside>
      <main>
        <Routes>
          <Route index element={<Dashboard />} />
          <Route path="domains" element={<Domains />} />
          <Route path="accounts" element={<Accounts />} />
          <Route path="queue" element={<Queue />} />
        </Routes>
      </main>
    </div>
  )
}

export default function App() {
  return <Routes><Route path="/login" element={<Login />} /><Route path="/*" element={<Shell />} /></Routes>
}
