import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { NavLink, Navigate, Route, Routes, useNavigate } from 'react-router-dom'
import { api, auth, crud, type Account, type Alias, type Domain, type QueueItem, type Stats } from './api'

function useLoad<T>(fn: () => Promise<T>) {
  const [data, setData] = useState<T>()
  const [error, setError] = useState('')
  const load = useCallback(() => { setError(''); fn().then(setData).catch(e => setError(e.message)) }, [])
  useEffect(load, [load])
  return { data, error, reload: load }
}

const fmtMb = (mb: number) => (mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${mb} MB`)

function Quota({ used, total }: { used: number; total: number }) {
  if (total === 0) return <p className="text-xs text-muted">{fmtMb(used)} · không giới hạn</p>
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

type Field = { key: string; label: string; type?: 'text' | 'password' | 'number' | 'checkbox'; required?: boolean; readOnly?: boolean; hint?: string }
type Values = Record<string, string | number | boolean>

function FormDialog({ title, fields, initial, onSave, onClose }: {
  title: string; fields: Field[]; initial: Values; onSave: (v: Values) => Promise<void>; onClose: () => void
}) {
  const [v, setV] = useState<Values>(initial)
  const [err, setErr] = useState('')
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    const h = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    addEventListener('keydown', h); return () => removeEventListener('keydown', h)
  }, [onClose])
  const submit = async (e: React.FormEvent) => {
    e.preventDefault(); setBusy(true); setErr('')
    try { await onSave(v); onClose() } catch (x) { setErr((x as Error).message); setBusy(false) }
  }
  const input = 'w-full rounded-lg border border-line px-3 py-2 text-sm read-only:bg-mist'
  return (
    <div role="dialog" aria-modal="true" aria-label={title} className="fixed inset-0 z-10 grid place-items-center bg-ink/50 p-4">
      <form onSubmit={submit} className="w-full max-w-md space-y-4 rounded-2xl bg-white p-6">
        <h2 className="text-lg font-semibold">{title}</h2>
        {fields.map((f, i) => f.type === 'checkbox' ? (
          <label key={f.key} className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={!!v[f.key]} onChange={e => setV({ ...v, [f.key]: e.target.checked })} />{f.label}
          </label>
        ) : (
          <label key={f.key} className="block text-sm">
            <span className="mb-1 block text-muted">{f.label}</span>
            <input className={input} type={f.type ?? 'text'} required={f.required} readOnly={f.readOnly} autoFocus={i === 0 && !f.readOnly}
              value={String(v[f.key] ?? '')} onChange={e => setV({ ...v, [f.key]: f.type === 'number' ? Number(e.target.value) : e.target.value })} />
            {f.hint && <span className="mt-1 block text-xs text-muted">{f.hint}</span>}
          </label>
        ))}
        {err && <p role="alert" className="text-sm text-warn">{err}</p>}
        <div className="flex justify-end gap-2">
          <button type="button" onClick={onClose} className="rounded-lg px-4 py-2 text-sm hover:bg-mist">Huỷ</button>
          <button disabled={busy} className="rounded-lg bg-ink px-4 py-2 text-sm font-medium text-white hover:bg-signal disabled:opacity-50">Lưu thay đổi</button>
        </div>
      </form>
    </div>
  )
}

function Head({ q, setQ, label, onAdd, addLabel }: { q: string; setQ: (v: string) => void; label: string; onAdd: () => void; addLabel: string }) {
  return (
    <div className="mb-4 flex flex-wrap items-center gap-3">
      <input aria-label={label} placeholder={label} value={q} onChange={e => setQ(e.target.value)} className="w-full max-w-sm rounded-lg border border-line bg-white px-3 py-2 text-sm" />
      <button onClick={onAdd} className="rounded-lg bg-ink px-4 py-2 text-sm font-medium text-white hover:bg-signal">{addLabel}</button>
    </div>
  )
}

function Actions({ onEdit, onDelete }: { onEdit: () => void; onDelete: () => void }) {
  return (
    <div className="flex gap-3 text-sm font-medium">
      <button onClick={onEdit} className="text-signal hover:underline">Sửa</button>
      <button onClick={onDelete} className="text-warn hover:underline">Xoá</button>
    </div>
  )
}

async function confirmDelete(message: string, run: () => Promise<void>, reload: () => void) {
  if (!confirm(message)) return
  try { await run() } catch (e) { alert((e as Error).message) }
  reload()
}

function Domains() {
  const { data, error, reload } = useLoad<Domain[]>(api.domains)
  const [q, setQ] = useState('')
  const [dlg, setDlg] = useState<Domain | 'new' | null>(null)
  const rows = (data ?? []).filter(d => d.name.includes(q.toLowerCase()))
  return (
    <Page title="Tên miền" error={error}>
      <Head q={q} setQ={setQ} label="Tìm tên miền" addLabel="Thêm tên miền" onAdd={() => setDlg('new')} />
      <Table heads={['Tên miền', 'Trạng thái', 'Tài khoản', 'Dung lượng', '']} empty="Chưa có tên miền nào."
        rows={rows.map(d => [<b key="n">{d.name}</b>, <Status key="s" active={d.active} />, d.accounts, <Quota key="q" used={d.usedMb} total={d.quotaMb} />,
          <Actions key="a" onEdit={() => setDlg(d)} onDelete={() => confirmDelete(`Xoá tên miền ${d.name} cùng ${d.accounts} tài khoản của nó?`, () => crud.deleteDomain(d.id), reload)} />])} />
      {dlg && <FormDialog title={dlg === 'new' ? 'Thêm tên miền' : `Sửa ${dlg.name}`} onClose={() => setDlg(null)}
        fields={[{ key: 'name', label: 'Tên miền', required: true }, { key: 'quotaMb', label: 'Dung lượng tối đa (MB)', type: 'number', hint: '0 = không giới hạn' }, { key: 'active', label: 'Đang hoạt động', type: 'checkbox' }]}
        initial={dlg === 'new' ? { name: '', quotaMb: 0, active: true } : { name: dlg.name, quotaMb: dlg.quotaMb, active: dlg.active }}
        onSave={async v => { await crud.saveDomain(dlg === 'new' ? null : dlg.id, { name: String(v.name).trim().toLowerCase(), quotaMb: Number(v.quotaMb), active: !!v.active }); reload() }} />}
    </Page>
  )
}

function Accounts() {
  const { data, error, reload } = useLoad<Account[]>(api.accounts)
  const [q, setQ] = useState('')
  const [dlg, setDlg] = useState<Account | 'new' | null>(null)
  const rows = (data ?? []).filter(a => a.address.includes(q.toLowerCase()))
  return (
    <Page title="Tài khoản" error={error}>
      <Head q={q} setQ={setQ} label="Tìm tài khoản" addLabel="Thêm tài khoản" onAdd={() => setDlg('new')} />
      <Table heads={['Địa chỉ', 'Trạng thái', 'Đăng nhập gần nhất', 'Dung lượng', '']} empty="Không tìm thấy tài khoản."
        rows={rows.map(a => [<b key="n">{a.address}</b>, <Status key="s" active={a.active} />, a.lastLogon, <Quota key="q" used={a.usedMb} total={a.quotaMb} />,
          <Actions key="a" onEdit={() => setDlg(a)} onDelete={() => confirmDelete(`Xoá tài khoản ${a.address} và toàn bộ thư?`, () => crud.deleteAccount(a.id), reload)} />])} />
      {dlg && <FormDialog title={dlg === 'new' ? 'Thêm tài khoản' : `Sửa ${dlg.address}`} onClose={() => setDlg(null)}
        fields={[{ key: 'address', label: 'Địa chỉ email', required: true, readOnly: dlg !== 'new' },
          { key: 'password', label: 'Mật khẩu', type: 'password', required: dlg === 'new', hint: dlg === 'new' ? 'Tối thiểu 8 ký tự' : 'Để trống nếu không đổi mật khẩu' },
          { key: 'quotaMb', label: 'Dung lượng tối đa (MB)', type: 'number', hint: '0 = không giới hạn' }, { key: 'active', label: 'Đang hoạt động', type: 'checkbox' }]}
        initial={dlg === 'new' ? { address: '', password: '', quotaMb: 1024, active: true } : { address: dlg.address, password: '', quotaMb: dlg.quotaMb, active: dlg.active }}
        onSave={async v => { await crud.saveAccount(dlg === 'new' ? null : dlg.id, { address: String(v.address).trim().toLowerCase(), password: String(v.password) || undefined, quotaMb: Number(v.quotaMb), active: !!v.active }); reload() }} />}
    </Page>
  )
}

function Aliases() {
  const { data, error, reload } = useLoad<Alias[]>(crud.aliases)
  const [q, setQ] = useState('')
  const [dlg, setDlg] = useState<Alias | 'new' | null>(null)
  const rows = (data ?? []).filter(a => a.name.includes(q.toLowerCase()) || a.value.includes(q.toLowerCase()))
  return (
    <Page title="Alias" error={error}>
      <Head q={q} setQ={setQ} label="Tìm alias" addLabel="Thêm alias" onAdd={() => setDlg('new')} />
      <Table heads={['Alias', 'Chuyển tới', 'Trạng thái', '']} empty="Chưa có alias nào."
        rows={rows.map(a => [<b key="n">{a.name}</b>, a.value, <Status key="s" active={a.active} />,
          <Actions key="a" onEdit={() => setDlg(a)} onDelete={() => confirmDelete(`Xoá alias ${a.name}?`, () => crud.deleteAlias(a.id), reload)} />])} />
      {dlg && <FormDialog title={dlg === 'new' ? 'Thêm alias' : `Sửa ${dlg.name}`} onClose={() => setDlg(null)}
        fields={[{ key: 'name', label: 'Địa chỉ alias', required: true }, { key: 'value', label: 'Chuyển tới', required: true }, { key: 'active', label: 'Đang hoạt động', type: 'checkbox' }]}
        initial={dlg === 'new' ? { name: '', value: '', active: true } : { name: dlg.name, value: dlg.value, active: dlg.active }}
        onSave={async v => { await crud.saveAlias(dlg === 'new' ? null : dlg.id, { name: String(v.name).trim().toLowerCase(), value: String(v.value).trim().toLowerCase(), active: !!v.active }); reload() }} />}
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

const nav = [['/', 'Tổng quan'], ['/domains', 'Tên miền'], ['/accounts', 'Tài khoản'], ['/aliases', 'Alias'], ['/queue', 'Hàng đợi gửi']]

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
          <Route path="aliases" element={<Aliases />} />
          <Route path="queue" element={<Queue />} />
        </Routes>
      </main>
    </div>
  )
}

export default function App() {
  return <Routes><Route path="/login" element={<Login />} /><Route path="/*" element={<Shell />} /></Routes>
}
