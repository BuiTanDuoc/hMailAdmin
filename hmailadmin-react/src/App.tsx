import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { NavLink, Navigate, Route, Routes, useNavigate } from 'react-router-dom'
import { api, auth, crud, ext, logsApi, tools, ops, more, type Dnsbl, type DiagCheck, type SslCert, type Rule, type RuleInput, type RuleCriteria, type RuleAction, type SettingRow, type SettingValue, type BlResult, type LogFile, type LogLine, type LogSummary, type Account, type Alias, type DistList, type IpRange, type SecuritySettings, type Domain, type QueueItem, type Stats } from './api'

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
  const [size, setSize] = useState(20)
  const [page, setPage] = useState(0)
  useEffect(() => setPage(0), [rows.length, size])
  const pages = Math.max(1, Math.ceil(rows.length / size))
  const cur = Math.min(page, pages - 1)
  const from = cur * size
  const btn = 'rounded-lg border border-line px-3 py-1.5 text-sm hover:bg-mist disabled:opacity-40 disabled:hover:bg-transparent'
  return (
    <div className="rounded-xl border border-line bg-white">
      <div className="overflow-x-auto">
        <table className="w-full text-left text-sm">
          <thead className="border-b border-line text-muted">
            <tr>{heads.map(h => <th key={h} className="px-4 py-3 font-medium">{h}</th>)}</tr>
          </thead>
          <tbody>
            {rows.length === 0 && <tr><td colSpan={heads.length} className="px-4 py-8 text-center text-muted">{empty}</td></tr>}
            {rows.slice(from, from + size).map((r, i) => (
              <tr key={from + i} className="border-b border-line/60 last:border-0 hover:bg-mist/60">
                {r.map((c, j) => <td key={j} className="px-4 py-3 align-middle">{c}</td>)}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {rows.length > 10 && (
        <div className="flex flex-wrap items-center justify-between gap-3 border-t border-line px-4 py-3 text-sm text-muted">
          <span>Hiển thị {from + 1}–{Math.min(from + size, rows.length)} / {rows.length}</span>
          <div className="flex items-center gap-2">
            <label className="flex items-center gap-2">Số dòng
              <select value={size} onChange={e => setSize(Number(e.target.value))} className="rounded-lg border border-line bg-white px-2 py-1.5 text-ink">
                {[10, 20, 50, 100].map(n => <option key={n} value={n}>{n}</option>)}
              </select>
            </label>
            <button className={btn} disabled={cur === 0} onClick={() => setPage(cur - 1)}>Trước</button>
            <span>Trang {cur + 1}/{pages}</span>
            <button className={btn} disabled={cur >= pages - 1} onClick={() => setPage(cur + 1)}>Sau</button>
          </div>
        </div>
      )}
    </div>
  )
}

function Search({ value, onChange, label }: { value: string; onChange: (v: string) => void; label: string }) {
  return <input aria-label={label} placeholder={label} value={value} onChange={e => onChange(e.target.value)}
    className="mb-4 w-full max-w-sm rounded-lg border border-line bg-white px-3 py-2 text-sm" />
}

function Spark({ data }: { data: number[] }) {
  if (data.length < 2) return <p className="py-4 text-sm text-muted">Đang thu thập dữ liệu…</p>
  const max = Math.max(...data, 1)
  const pts = data.map((v, i) => `${(i / (data.length - 1)) * 300},${60 - (v / max) * 54}`).join(' ')
  return <svg viewBox="0 0 300 64" className="h-16 w-full" role="img" aria-label="Phiên kết nối theo thời gian">
    <polyline points={pts} fill="none" stroke="var(--color-signal)" strokeWidth="2.5" strokeLinejoin="round" strokeLinecap="round" />
  </svg>
}

function Dashboard() {
  const { data, error, reload } = useLoad<Stats>(api.stats)
  useEffect(() => { const t = setInterval(reload, 15000); return () => clearInterval(t) }, [reload])
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

type Field = { key: string; label: string; type?: 'text' | 'password' | 'number' | 'checkbox' | 'textarea'; required?: boolean; readOnly?: boolean; hint?: string }
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
        {fields.map((f, i) => f.type === 'textarea' ? (
          <label key={f.key} className="block text-sm">
            <span className="mb-1 block text-muted">{f.label}</span>
            <textarea className={input} rows={5} value={String(v[f.key] ?? '')} onChange={e => setV({ ...v, [f.key]: e.target.value })} />
            {f.hint && <span className="mt-1 block text-xs text-muted">{f.hint}</span>}
          </label>
        ) : f.type === 'checkbox' ? (
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

function Head({ q, setQ, label, onAdd, addLabel, extra }: { q: string; setQ: (v: string) => void; label: string; onAdd: () => void; addLabel: string; extra?: ReactNode }) {
  return (
    <div className="mb-4 flex flex-wrap items-center gap-3">
      <input aria-label={label} placeholder={label} value={q} onChange={e => setQ(e.target.value)} className="w-full max-w-sm rounded-lg border border-line bg-white px-3 py-2 text-sm" />
      {extra}
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
  const { data: domains } = useLoad<Domain[]>(api.domains)
  const [q, setQ] = useState('')
  const [dom, setDom] = useState('')
  const [dlg, setDlg] = useState<Account | 'new' | null>(null)
  const rows = (data ?? []).filter(a => (!dom || a.address.toLowerCase().endsWith('@' + dom)) && a.address.includes(q.toLowerCase()))
  return (
    <Page title="Tài khoản" error={error}>
      <Head q={q} setQ={setQ} label="Tìm tài khoản" addLabel="Thêm tài khoản" onAdd={() => setDlg('new')}
        extra={<select aria-label="Lọc theo tên miền" value={dom} onChange={e => setDom(e.target.value)} className="rounded-lg border border-line bg-white px-3 py-2 text-sm">
          <option value="">Tất cả</option>
          {(domains ?? []).map(d => <option key={d.id} value={d.name}>{d.name}</option>)}
        </select>} />
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

const lines = (t: string) => t.split(/[\s,;]+/).map(x => x.trim().toLowerCase()).filter(Boolean)

function Lists() {
  const { data, error, reload } = useLoad<DistList[]>(ext.lists)
  const [q, setQ] = useState('')
  const [dlg, setDlg] = useState<DistList | 'new' | null>(null)
  const rows = (data ?? []).filter(l => l.address.includes(q.toLowerCase()))
  return (
    <Page title="Danh sách phân phối" error={error}>
      <Head q={q} setQ={setQ} label="Tìm danh sách" addLabel="Thêm danh sách" onAdd={() => setDlg('new')} />
      <Table heads={['Địa chỉ', 'Trạng thái', 'Thành viên', '']} empty="Chưa có danh sách phân phối."
        rows={rows.map(l => [<b key="n">{l.address}</b>, <Status key="s" active={l.active} />, `${l.members.length} địa chỉ`,
          <Actions key="a" onEdit={() => setDlg(l)} onDelete={() => confirmDelete(`Xoá danh sách ${l.address}?`, () => ext.deleteList(l.id), reload)} />])} />
      {dlg && <FormDialog title={dlg === 'new' ? 'Thêm danh sách phân phối' : `Sửa ${dlg.address}`} onClose={() => setDlg(null)}
        fields={[{ key: 'address', label: 'Địa chỉ danh sách', required: true }, { key: 'members', label: 'Thành viên', type: 'textarea', hint: 'Mỗi địa chỉ một dòng' }, { key: 'active', label: 'Đang hoạt động', type: 'checkbox' }]}
        initial={dlg === 'new' ? { address: '', members: '', active: true } : { address: dlg.address, members: dlg.members.join('\n'), active: dlg.active }}
        onSave={async v => { await ext.saveList(dlg === 'new' ? null : dlg.id, { address: String(v.address).trim().toLowerCase(), members: lines(String(v.members)), active: !!v.active }); reload() }} />}
    </Page>
  )
}

function Ranges() {
  const { data, error, reload } = useLoad<IpRange[]>(ext.ranges)
  const [q, setQ] = useState('')
  const [dlg, setDlg] = useState<IpRange | 'new' | null>(null)
  const rows = (data ?? []).filter(r => (r.name + r.lowerIp + r.upperIp).toLowerCase().includes(q.toLowerCase()))
  const svc = (r: IpRange) => [r.allowSmtp && 'SMTP', r.allowPop && 'POP3', r.allowImap && 'IMAP'].filter(Boolean).join(', ') || 'Không'
  return (
    <Page title="IP ranges" error={error}>
      <Head q={q} setQ={setQ} label="Tìm theo tên hoặc IP" addLabel="Thêm IP range" onAdd={() => setDlg('new')} />
      <Table heads={['Tên', 'Dải IP', 'Ưu tiên', 'Cho phép kết nối', 'Hết hạn', '']} empty="Chưa có IP range."
        rows={rows.map(r => [<b key="n">{r.name}</b>, `${r.lowerIp} – ${r.upperIp}`, r.priority, svc(r), r.expires ?? '—',
          <Actions key="a" onEdit={() => setDlg(r)} onDelete={() => confirmDelete(`Xoá IP range ${r.name}?`, () => ext.deleteRange(r.id), reload)} />])} />
      {dlg && <FormDialog title={dlg === 'new' ? 'Thêm IP range' : `Sửa ${dlg.name}`} onClose={() => setDlg(null)}
        fields={[{ key: 'name', label: 'Tên', required: true }, { key: 'lowerIp', label: 'IP bắt đầu', required: true }, { key: 'upperIp', label: 'IP kết thúc', required: true },
          { key: 'priority', label: 'Ưu tiên', type: 'number', hint: 'Số lớn hơn được áp dụng trước' },
          { key: 'allowSmtp', label: 'Cho phép SMTP', type: 'checkbox' }, { key: 'allowPop', label: 'Cho phép POP3', type: 'checkbox' }, { key: 'allowImap', label: 'Cho phép IMAP', type: 'checkbox' }]}
        initial={dlg === 'new' ? { name: '', lowerIp: '', upperIp: '', priority: 10, allowSmtp: true, allowPop: true, allowImap: true }
          : { name: dlg.name, lowerIp: dlg.lowerIp, upperIp: dlg.upperIp, priority: dlg.priority, allowSmtp: dlg.allowSmtp, allowPop: dlg.allowPop, allowImap: dlg.allowImap }}
        onSave={async v => { await ext.saveRange(dlg === 'new' ? null : dlg.id, { name: String(v.name).trim(), lowerIp: String(v.lowerIp).trim(), upperIp: String(v.upperIp).trim(), priority: Number(v.priority), allowSmtp: !!v.allowSmtp, allowPop: !!v.allowPop, allowImap: !!v.allowImap }); reload() }} />}
    </Page>
  )
}

function Security() {
  const { data, error } = useLoad<SecuritySettings>(ext.security)
  const [v, setV] = useState<SecuritySettings>()
  const [msg, setMsg] = useState('')
  useEffect(() => setV(data), [data])
  const num = (k: keyof SecuritySettings, label: string) => (
    <label key={String(k)} className="block text-sm"><span className="mb-1 block text-muted">{label}</span>
      <input type="number" min={0} value={Number(v![k])} onChange={e => setV({ ...v!, [k]: Number(e.target.value) })} className="w-40 rounded-lg border border-line px-3 py-2" /></label>
  )
  const check = (k: keyof SecuritySettings, label: string) => (
    <label key={String(k)} className="flex items-center gap-2 text-sm"><input type="checkbox" checked={!!v![k]} onChange={e => setV({ ...v!, [k]: e.target.checked })} />{label}</label>
  )
  const save = async () => { try { await ext.saveSecurity(v!); setMsg('Đã lưu thay đổi.') } catch (e) { setMsg((e as Error).message) } }
  return (
    <Page title="Chống tấn công" error={error}>
      {!v ? <p className="text-muted">Đang tải…</p> : (
        <div className="max-w-xl space-y-6">
          <div className="space-y-4 rounded-xl border border-line bg-white p-6">
            <h2 className="font-semibold">Tự động chặn IP (auto-ban)</h2>
            {check('autoBan', 'Chặn IP khi đăng nhập sai nhiều lần')}
            {num('maxAttempts', 'Số lần sai tối đa')}{num('withinMinutes', 'Trong vòng (phút)')}{num('banMinutes', 'Thời gian chặn (phút)')}
          </div>
          <div className="space-y-4 rounded-xl border border-line bg-white p-6">
            <h2 className="font-semibold">Greylisting</h2>
            {check('greyEnabled', 'Bật greylisting')}
            {num('greyInitialDelayMin', 'Trì hoãn ban đầu (phút)')}{num('greyInitialDeleteHours', 'Xoá bản ghi chưa xác nhận sau (giờ)')}{num('greyFinalDeleteHours', 'Xoá bản ghi đã xác nhận sau (giờ)')}
          </div>
          <div className="flex items-center gap-4">
            <button onClick={save} className="rounded-lg bg-ink px-4 py-2 text-sm font-medium text-white hover:bg-signal">Lưu thay đổi</button>
            {msg && <p role="status" className="text-sm text-muted">{msg}</p>}
          </div>
        </div>
      )}
    </Page>
  )
}

function Logs() {
  const files = useLoad<LogFile[]>(logsApi.files)
  const [file, setFile] = useState('')
  const [sum, setSum] = useState<LogSummary>()
  const [lines, setLines] = useState<LogLine[]>([])
  const [f, setF] = useState({ q: '', type: '', ip: '' })
  const [err, setErr] = useState('')
  const search = (query = f) => logsApi.search(file, query).then(setLines).catch(e => setErr(e.message))
  useEffect(() => { if (!file && files.data?.length) setFile(files.data[0].name) }, [files.data, file])
  useEffect(() => {
    if (!file) return
    const empty = { q: '', type: '', ip: '' }
    setSum(undefined); setErr(''); setF(empty)
    logsApi.summary(file).then(setSum).catch(e => setErr(e.message))
    logsApi.search(file, empty).then(setLines).catch(e => setErr(e.message))
  }, [file])
  const look = (ip: string) => { const n = { q: '', type: '', ip }; setF(n); search(n) }
  const block = async (ip: string) => {
    if (!confirm(`Chặn ${ip}? Mọi kết nối SMTP/POP3/IMAP từ IP này sẽ bị từ chối. Đừng chặn IP của chính bạn.`)) return
    try {
      await ext.saveRange(null, { name: `Chặn ${ip}`, lowerIp: ip, upperIp: ip, priority: 90, allowSmtp: false, allowPop: false, allowImap: false })
      alert(`Đã thêm IP range chặn ${ip}.`)
    } catch (e) { alert((e as Error).message) }
  }
  const field = 'rounded-lg border border-line bg-white px-3 py-2 text-sm'
  return (
    <Page title="Nhật ký" error={files.error || err}>
      <select aria-label="Chọn file log" value={file} onChange={e => setFile(e.target.value)} className={`mb-6 ${field}`}>
        {(files.data ?? []).map(x => <option key={x.name} value={x.name}>{x.name} ({(x.size / 1048576).toFixed(1)} MB)</option>)}
      </select>
      {!sum ? <p className="text-muted">Đang phân tích…</p> : (
        <div className="space-y-8">
          <dl className="grid gap-4 sm:grid-cols-4">
            {[['Tổng số dòng', sum.lines], ['Đăng nhập thất bại', sum.failedTotal], ['Lỗi', sum.errorsTotal], ['Lượt gửi thư', sum.deliveries]].map(([k, v]) => (
              <div key={k} className="rounded-xl border border-line bg-white p-4">
                <dt className="text-sm text-muted">{k}</dt><dd className="mt-1 text-2xl font-semibold">{Number(v).toLocaleString('vi-VN')}</dd>
              </div>
            ))}
          </dl>
          {sum.failedLogins.length > 0 && (
            <div><h2 className="mb-3 font-semibold">Đăng nhập thất bại theo IP</h2>
              <Table heads={['IP', 'Giao thức', 'Số lần', 'Lần cuối', '']} empty=""
                rows={sum.failedLogins.map(x => [x.ip, x.protocol, x.count, x.last,
                  <div key="a" className="flex gap-3 text-sm font-medium">
                    <button onClick={() => look(x.ip)} className="text-signal hover:underline">Xem log</button>
                    <button onClick={() => block(x.ip)} className="text-warn hover:underline">Chặn</button>
                  </div>])} /></div>
          )}
          {sum.errors.length > 0 && (
            <div><h2 className="mb-3 font-semibold">Lỗi ghi nhận</h2>
              <Table heads={['Mã lỗi', 'Mức độ', 'Số lần', 'Ví dụ']} empty=""
                rows={sum.errors.map(x => [<b key="c">{x.code}</b>, x.severity, x.count, <span key="s" className="break-all text-muted">{x.sample}</span>])} /></div>
          )}
          <div><h2 className="mb-3 font-semibold">Tìm trong log <span className="text-sm font-normal text-muted">(tối đa 200 dòng mới nhất)</span></h2>
            <form onSubmit={e => { e.preventDefault(); search() }} className="mb-4 flex flex-wrap gap-3">
              <input aria-label="Từ khoá" placeholder="Từ khoá" value={f.q} onChange={e => setF({ ...f, q: e.target.value })} className={field} />
              <input aria-label="IP" placeholder="IP" value={f.ip} onChange={e => setF({ ...f, ip: e.target.value })} className={field} />
              <select aria-label="Loại" value={f.type} onChange={e => setF({ ...f, type: e.target.value })} className={field}>
                <option value="">Mọi loại</option>{Object.keys(sum.byType).map(t => <option key={t}>{t}</option>)}
              </select>
              <button className="rounded-lg bg-ink px-4 py-2 text-sm font-medium text-white hover:bg-signal">Tìm</button>
            </form>
            <Table heads={['Thời gian', 'Loại', 'IP', 'Nội dung']} empty="Không có dòng log phù hợp."
              rows={lines.map(l => [l.time, l.type, l.ip ?? '—', <span key="m" className="break-all">{l.message}</span>])} /></div>
        </div>
      )}
    </Page>
  )
}

const FIELDS: [string, string][] = [['from', 'Người gửi (From)'], ['to', 'Người nhận (To)'], ['cc', 'CC'], ['subject', 'Tiêu đề'], ['body', 'Nội dung'],
  ['size', 'Kích thước (byte)'], ['recipients', 'Người nhận envelope'], ['attempts', 'Lần gửi thứ'], ['header', 'Header tuỳ chỉnh']]
const MATCHES: [string, string][] = [['equals', 'bằng'], ['contains', 'chứa'], ['notequals', 'không bằng'], ['notcontains', 'không chứa'],
  ['lt', 'nhỏ hơn'], ['gt', 'lớn hơn'], ['wildcard', 'khớp wildcard'], ['regex', 'khớp regex']]
const ACTS: [string, string][] = [['move', 'Chuyển vào thư mục IMAP'], ['forward', 'Chuyển tiếp tới'], ['setheader', 'Đặt giá trị header'],
  ['copy', 'Tạo bản sao'], ['delete', 'Xoá thư'], ['stop', 'Dừng xử lý các rule sau']]
const label = (list: [string, string][], k: string) => list.find(x => x[0] === k)?.[1] ?? k
const blankCrit: RuleCriteria = { field: 'subject', header: '', match: 'contains', value: '' }
const blankAct: RuleAction = { type: 'move', to: '', folder: '', header: '', value: '' }

function RuleDialog({ title, initial, onSave, onClose }: { title: string; initial: RuleInput; onSave: (v: RuleInput) => Promise<void>; onClose: () => void }) {
  const [v, setV] = useState(initial)
  const [err, setErr] = useState('')
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    const h = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    addEventListener('keydown', h); return () => removeEventListener('keydown', h)
  }, [onClose])
  const setC = (i: number, p: Partial<RuleCriteria>) => setV({ ...v, criteria: v.criteria.map((c, j) => (j === i ? { ...c, ...p } : c)) })
  const setA = (i: number, p: Partial<RuleAction>) => setV({ ...v, actions: v.actions.map((a, j) => (j === i ? { ...a, ...p } : a)) })
  const submit = async (e: React.FormEvent) => {
    e.preventDefault(); setBusy(true); setErr('')
    try { await onSave(v); onClose() } catch (x) { setErr((x as Error).message); setBusy(false) }
  }
  const f = 'rounded-lg border border-line bg-white px-2 py-2 text-sm'
  const link = 'text-sm font-medium text-signal hover:underline'
  return (
    <div role="dialog" aria-modal="true" aria-label={title} className="fixed inset-0 z-10 overflow-y-auto bg-ink/50 p-4">
      <form onSubmit={submit} className="mx-auto max-w-3xl space-y-5 rounded-2xl bg-white p-6">
        <h2 className="text-lg font-semibold">{title}</h2>
        <div className="flex flex-wrap items-center gap-3">
          <input aria-label="Tên rule" required autoFocus placeholder="Tên rule" value={v.name} onChange={e => setV({ ...v, name: e.target.value })} className={`${f} min-w-60 flex-1`} />
          <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={v.active} onChange={e => setV({ ...v, active: e.target.checked })} />Đang bật</label>
          <select aria-label="Kiểu khớp" value={v.matchAll ? 'all' : 'any'} onChange={e => setV({ ...v, matchAll: e.target.value === 'all' })} className={f}>
            <option value="all">Khớp TẤT CẢ điều kiện (AND)</option><option value="any">Khớp MỘT trong các điều kiện (OR)</option>
          </select>
        </div>
        <section className="space-y-2"><h3 className="text-sm font-semibold text-muted">NẾU</h3>
          {v.criteria.map((c, i) => (
            <div key={i} className="flex flex-wrap gap-2">
              <select aria-label="Trường" value={c.field} onChange={e => setC(i, { field: e.target.value })} className={f}>{FIELDS.map(([k, t]) => <option key={k} value={k}>{t}</option>)}</select>
              {c.field === 'header' && <input aria-label="Tên header" placeholder="Tên header (không có dấu :)" value={c.header} onChange={e => setC(i, { header: e.target.value })} className={f} />}
              <select aria-label="So khớp" value={c.match} onChange={e => setC(i, { match: e.target.value })} className={f}>{MATCHES.map(([k, t]) => <option key={k} value={k}>{t}</option>)}</select>
              <input aria-label="Giá trị" placeholder="Giá trị" value={c.value} onChange={e => setC(i, { value: e.target.value })} className={`${f} min-w-40 flex-1`} />
              <button type="button" aria-label="Xoá điều kiện" onClick={() => setV({ ...v, criteria: v.criteria.filter((_, j) => j !== i) })} className="px-2 text-warn">✕</button>
            </div>
          ))}
          <button type="button" className={link} onClick={() => setV({ ...v, criteria: [...v.criteria, { ...blankCrit }] })}>+ Thêm điều kiện</button>
        </section>
        <section className="space-y-2"><h3 className="text-sm font-semibold text-muted">THÌ</h3>
          {v.actions.map((a, i) => (
            <div key={i} className="flex flex-wrap gap-2">
              <select aria-label="Hành động" value={a.type} onChange={e => setA(i, { type: e.target.value })} className={f}>{ACTS.map(([k, t]) => <option key={k} value={k}>{t}</option>)}</select>
              {a.type === 'move' && <input aria-label="Thư mục" placeholder="vd. INBOX.Junk" value={a.folder} onChange={e => setA(i, { folder: e.target.value })} className={`${f} min-w-40 flex-1`} />}
              {a.type === 'forward' && <input aria-label="Địa chỉ" placeholder="địa chỉ email" value={a.to} onChange={e => setA(i, { to: e.target.value })} className={`${f} min-w-40 flex-1`} />}
              {a.type === 'setheader' && <>
                <input aria-label="Tên header" placeholder="Tên header" value={a.header} onChange={e => setA(i, { header: e.target.value })} className={f} />
                <input aria-label="Giá trị header" placeholder="Giá trị" value={a.value} onChange={e => setA(i, { value: e.target.value })} className={`${f} min-w-40 flex-1`} /></>}
              <button type="button" aria-label="Xoá hành động" onClick={() => setV({ ...v, actions: v.actions.filter((_, j) => j !== i) })} className="px-2 text-warn">✕</button>
            </div>
          ))}
          <button type="button" className={link} onClick={() => setV({ ...v, actions: [...v.actions, { ...blankAct }] })}>+ Thêm hành động</button>
        </section>
        <ul className="list-disc space-y-1 pl-5 text-xs text-muted">
          <li>Rule khớp không tự dừng: thêm hành động "Dừng xử lý các rule sau" nếu muốn kết thúc tại rule này.</li>
          <li>"Không chứa" và "không bằng" đúng cả khi trường trống. Đừng dùng riêng chúng với hành động Xoá thư.</li>
          <li>Kích thước tính bằng byte (25 MB = 26214400). Regex phải khớp toàn bộ giá trị và phân biệt hoa thường.</li>
        </ul>
        {err && <p role="alert" className="text-sm text-warn">{err}</p>}
        <div className="flex justify-end gap-2">
          <button type="button" onClick={onClose} className="rounded-lg px-4 py-2 text-sm hover:bg-mist">Huỷ</button>
          <button disabled={busy} className="rounded-lg bg-ink px-4 py-2 text-sm font-medium text-white hover:bg-signal disabled:opacity-50">Lưu rule</button>
        </div>
      </form>
    </div>
  )
}

function Rules() {
  const { data, error, reload } = useLoad<Rule[]>(tools.rules)
  const [dlg, setDlg] = useState<Rule | 'new' | null>(null)
  const move = async (id: number, dir: 'up' | 'down') => { try { await tools.moveRule(id, dir) } catch (e) { alert((e as Error).message) } reload() }
  const toggle = async (r: Rule) => {
    if (!r.supported) return
    try { await tools.saveRule(r.id, { name: r.name, active: !r.active, matchAll: r.matchAll, criteria: r.criteria, actions: r.actions }) } catch (e) { alert((e as Error).message) }
    reload()
  }
  const rows = data ?? []
  return (
    <Page title="Rules" error={error}>
      <p className="mb-4 max-w-2xl text-sm text-muted">Rule toàn máy chủ, chạy từ trên xuống dưới cho mọi thư. Rule dùng hành động chưa hỗ trợ ở đây (trả lời, chạy script, route, bind IP) chỉ có thể xoá hoặc đổi thứ tự để tránh mất cấu hình.</p>
      <div className="mb-4"><button onClick={() => setDlg('new')} className="rounded-lg bg-ink px-4 py-2 text-sm font-medium text-white hover:bg-signal">Thêm rule</button></div>
      <Table heads={['#', 'Tên', 'Tóm tắt', 'Trạng thái', '']} empty="Chưa có rule nào."
        rows={rows.map((r, i) => [i + 1, <b key="n">{r.name}</b>,
          r.supported ? `${r.criteria.length} điều kiện → ${r.actions.map(a => label(ACTS, a.type)).join(', ')}` : <span key="u" className="text-warn">Có điều kiện hoặc hành động chưa hỗ trợ</span>,
          <button key="s" onClick={() => toggle(r)} disabled={!r.supported} className="disabled:opacity-60"><Status active={r.active} /></button>,
          <div key="a" className="flex gap-3 text-sm font-medium">
            <button aria-label="Lên" onClick={() => move(r.id, 'up')} disabled={i === 0} className="text-muted disabled:opacity-30">↑</button>
            <button aria-label="Xuống" onClick={() => move(r.id, 'down')} disabled={i === rows.length - 1} className="text-muted disabled:opacity-30">↓</button>
            <button onClick={() => setDlg(r)} disabled={!r.supported} className="text-signal hover:underline disabled:opacity-40">Sửa</button>
            <button onClick={() => confirmDelete(`Xoá rule "${r.name}"?`, () => tools.deleteRule(r.id), reload)} className="text-warn hover:underline">Xoá</button>
          </div>])} />
      {dlg && <RuleDialog title={dlg === 'new' ? 'Thêm rule' : `Sửa rule ${dlg.name}`} onClose={() => setDlg(null)}
        initial={dlg === 'new' ? { name: '', active: true, matchAll: true, criteria: [{ ...blankCrit }], actions: [{ ...blankAct }] }
          : { name: dlg.name, active: dlg.active, matchAll: dlg.matchAll, criteria: dlg.criteria, actions: dlg.actions }}
        onSave={async v => { await tools.saveRule(dlg === 'new' ? null : dlg.id, v); reload() }} />}
    </Page>
  )
}

function Settings() {
  const { data, error } = useLoad<SettingRow[]>(tools.settings)
  const [vals, setVals] = useState<Record<string, SettingValue>>({})
  const [errs, setErrs] = useState<Record<string, string>>({})
  const [msg, setMsg] = useState('')
  useEffect(() => {
    if (!data) return
    setVals(Object.fromEntries(data.filter(r => r.value !== null).map(r => [r.key, r.value as SettingValue])))
    setErrs(Object.fromEntries(data.filter(r => r.error).map(r => [r.key, r.error as string])))
  }, [data])
  const save = async () => {
    try {
      const res = await tools.saveSettings(vals)
      setErrs(res.errors)
      setMsg(Object.keys(res.errors).length ? 'Một số mục không lưu được (xem thông báo đỏ).' : 'Đã lưu thay đổi.')
    } catch (e) { setMsg((e as Error).message) }
  }
  const groups = [...new Set((data ?? []).map(r => r.group))]
  const input = 'w-full max-w-sm rounded-lg border border-line px-3 py-2 text-sm'
  return (
    <Page title="Cài đặt" error={error}>
      {!data ? <p className="text-muted">Đang tải…</p> : (
        <div className="max-w-2xl space-y-6">
          {groups.map(g => (
            <div key={g} className="space-y-4 rounded-xl border border-line bg-white p-6">
              <h2 className="font-semibold">{g}</h2>
              {data.filter(r => r.group === g).map(r => (
                <div key={r.key}>
                  {r.type === 'bool' ? (
                    <label className="flex items-center gap-2 text-sm"><input type="checkbox" disabled={r.value === null} checked={!!vals[r.key]} onChange={e => setVals({ ...vals, [r.key]: e.target.checked })} />{r.label}</label>
                  ) : (
                    <label className="block text-sm"><span className="mb-1 block text-muted">{r.label}</span>
                      <input className={input} disabled={r.value === null} type={r.type === 'int' ? 'number' : 'text'} value={String(vals[r.key] ?? '')}
                        onChange={e => setVals({ ...vals, [r.key]: r.type === 'int' ? Number(e.target.value) : e.target.value })} /></label>
                  )}
                  {errs[r.key] && <p role="alert" className="mt-1 text-xs text-warn">{r.key}: {errs[r.key]}</p>}
                </div>
              ))}
            </div>
          ))}
          <div className="flex items-center gap-4">
            <button onClick={save} className="rounded-lg bg-ink px-4 py-2 text-sm font-medium text-white hover:bg-signal">Lưu thay đổi</button>
            {msg && <p role="status" className="text-sm text-muted">{msg}</p>}
          </div>
        </div>
      )}
    </Page>
  )
}

function Utilities() {
  const { data: srv, error, reload } = useLoad<{ state: number }>(tools.server)
  const [ip, setIp] = useState('')
  const [bl, setBl] = useState<BlResult[]>()
  const [busy, setBusy] = useState('')
  const run = async (key: string, f: () => Promise<unknown>, ok: string) => {
    setBusy(key)
    try { await f(); alert(ok) } catch (e) { alert((e as Error).message) }
    setBusy(''); reload()
  }
  const states: Record<number, string> = { 0: 'Không rõ', 1: 'Đã dừng', 2: 'Đang khởi động', 3: 'Đang chạy', 4: 'Đang dừng' }
  const card = 'space-y-3 rounded-xl border border-line bg-white p-6'
  const btn = 'rounded-lg bg-ink px-4 py-2 text-sm font-medium text-white hover:bg-signal disabled:opacity-50'
  const tone = { listed: 'text-warn', clean: 'text-signal', error: 'text-muted' }
  const text = { listed: 'Bị liệt kê', clean: 'Sạch', error: 'Không kiểm tra được' }
  return (
    <Page title="Tiện ích" error={error}>
      <div className="max-w-3xl space-y-6">
        <div className={card}>
          <h2 className="font-semibold">Dịch vụ hMailServer</h2>
          <p className="text-sm text-muted">Trạng thái: <b className="text-ink">{srv ? (states[srv.state] ?? srv.state) : '…'}</b></p>
          <div className="flex gap-3">
            <button className={btn} disabled={!!busy} onClick={() => confirm('Dừng nhận và gửi thư?') && run('stop', () => tools.serverAction('stop'), 'Đã gửi lệnh dừng.')}>Dừng</button>
            <button className={btn} disabled={!!busy} onClick={() => run('start', () => tools.serverAction('start'), 'Đã gửi lệnh khởi động.')}>Khởi động</button>
          </div>
        </div>
        <div className={card}>
          <h2 className="font-semibold">Sao lưu</h2>
          <p className="text-sm text-muted">Chạy sao lưu theo cấu hình Backup hiện có trong hMailServer (thư mục đích, mục cần sao lưu).</p>
          <button className={btn} disabled={!!busy} onClick={() => run('backup', tools.backup, 'Đã yêu cầu sao lưu.')}>Chạy sao lưu ngay</button>
        </div>
        <div className={card}>
          <h2 className="font-semibold">Kiểm tra blacklist (DNSBL)</h2>
          <form className="flex flex-wrap gap-3" onSubmit={async e => {
            e.preventDefault(); setBusy('bl'); setBl(undefined)
            try { setBl(await tools.blacklist(ip.trim())) } catch (x) { alert((x as Error).message) }
            setBusy('')
          }}>
            <input aria-label="IPv4" required placeholder="IP công khai của máy chủ mail" value={ip} onChange={e => setIp(e.target.value)} className="w-64 rounded-lg border border-line px-3 py-2 text-sm" />
            <button className={btn} disabled={busy === 'bl'}>{busy === 'bl' ? 'Đang kiểm tra…' : 'Kiểm tra'}</button>
          </form>
          {bl && <Table heads={['Danh sách', 'Kết quả', 'Chi tiết']} empty="" rows={bl.map(r => [r.zone, <b key="s" className={tone[r.status]}>{text[r.status]}</b>, <span key="d" className="break-all text-muted">{r.detail}</span>])} />}
        </div>
      </div>
    </Page>
  )
}

const diagTone = { ok: ['Đạt', 'text-signal'], warn: ['Cảnh báo', 'text-warn'], fail: ['Lỗi', 'text-warn'], info: ['Thông tin', 'text-muted'] } as const

function Diagnostics() {
  const { data: domains, error } = useLoad<Domain[]>(api.domains)
  const [domain, setDomain] = useState('')
  const [serverIp, setServerIp] = useState('')
  const [selector, setSelector] = useState('')
  const [res, setRes] = useState<DiagCheck[]>()
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState('')
  useEffect(() => { if (!domain && domains?.length) setDomain(domains[0].name) }, [domains, domain])
  const run = async (e: React.FormEvent) => {
    e.preventDefault(); setBusy(true); setErr(''); setRes(undefined)
    try { setRes(await ops.diagnose({ domain, serverIp: serverIp.trim(), selector: selector.trim() })) } catch (x) { setErr((x as Error).message) }
    setBusy(false)
  }
  const f = 'rounded-lg border border-line bg-white px-3 py-2 text-sm'
  const count = (k: DiagCheck['status']) => res?.filter(r => r.status === k).length ?? 0
  return (
    <Page title="Chẩn đoán tên miền" error={error}>
      <form onSubmit={run} className="mb-6 flex flex-wrap items-end gap-3">
        <label className="text-sm"><span className="mb-1 block text-muted">Tên miền</span>
          <select aria-label="Tên miền" value={domain} onChange={e => setDomain(e.target.value)} className={f}>{(domains ?? []).map(d => <option key={d.id}>{d.name}</option>)}</select></label>
        <label className="text-sm"><span className="mb-1 block text-muted">IP công khai của máy chủ (tuỳ chọn)</span>
          <input value={serverIp} onChange={e => setServerIp(e.target.value)} placeholder="vd. 203.0.113.10" className={`${f} w-52`} /></label>
        <label className="text-sm"><span className="mb-1 block text-muted">Selector DKIM (tuỳ chọn)</span>
          <input value={selector} onChange={e => setSelector(e.target.value)} placeholder="vd. mail" className={`${f} w-40`} /></label>
        <button disabled={busy || !domain} className="rounded-lg bg-ink px-4 py-2 text-sm font-medium text-white hover:bg-signal disabled:opacity-50">{busy ? 'Đang kiểm tra…' : 'Chạy chẩn đoán'}</button>
      </form>
      {err && <p role="alert" className="mb-4 text-sm text-warn">{err}</p>}
      {res && <>
        <p className="mb-3 text-sm text-muted">{count('ok')} đạt · {count('warn')} cảnh báo · {count('fail')} lỗi</p>
        <Table heads={['Kiểm tra', 'Kết quả', 'Chi tiết']} empty="" rows={res.map(r => [r.title, <b key="s" className={diagTone[r.status][1]}>{diagTone[r.status][0]}</b>, <span key="d" className="break-all text-muted">{r.detail}</span>])} />
        <p className="mt-3 max-w-2xl text-xs text-muted">Kiểm tra cổng 25 chạy từ chính máy chủ này nên có thể khác với kết quả nhìn từ Internet (NAT, tường lửa). Các bản ghi DNS lấy trực tiếp từ DNS, có thể chưa cập nhật nếu vừa thay đổi.</p>
      </>}
    </Page>
  )
}

function Ssl() {
  const { data, error, reload } = useLoad<SslCert[]>(ops.certs)
  const [dlg, setDlg] = useState<SslCert | 'new' | null>(null)
  const expiry = (c: SslCert) => {
    const d = c.info.daysLeft
    if (d === null || !c.info.notAfter) return '—'
    return <span className={d <= 30 ? 'font-medium text-warn' : ''}>{c.info.notAfter} ({d < 0 ? 'đã hết hạn' : `còn ${d} ngày`})</span>
  }
  return (
    <Page title="Chứng chỉ SSL" error={error}>
      <div className="mb-4"><button onClick={() => setDlg('new')} className="rounded-lg bg-ink px-4 py-2 text-sm font-medium text-white hover:bg-signal">Thêm chứng chỉ</button></div>
      <Table heads={['Tên', 'Chủ thể / tên miền', 'Hết hạn', 'Tình trạng', '']} empty="Chưa có chứng chỉ SSL."
        rows={(data ?? []).map(c => [<b key="n">{c.name}</b>,
          <span key="s" className="break-all">{c.info.names.join(', ') || c.info.subject || '—'}</span>, expiry(c),
          <div key="t" className="space-y-0.5 text-xs">
            {c.info.hostMatch === false && <p className="text-warn">Không khớp host name của máy chủ</p>}
            {c.info.keyOk && <p className="text-signal">Khoá riêng khớp</p>}
            {c.info.problem && <p className="text-warn">{c.info.problem}</p>}
          </div>,
          <Actions key="a" onEdit={() => setDlg(c)} onDelete={() => confirmDelete(`Xoá chứng chỉ ${c.name}?`, () => ops.deleteCert(c.id), reload)} />])} />
      <p className="mt-3 max-w-2xl text-xs text-muted">Đường dẫn file là đường dẫn trên máy chủ hMailServer. Chứng chỉ chỉ có tác dụng khi một cổng TCP/IP được cấu hình dùng nó; thay đổi có thể cần khởi động lại dịch vụ hMailServer.</p>
      {dlg && <FormDialog title={dlg === 'new' ? 'Thêm chứng chỉ SSL' : `Sửa ${dlg.name}`} onClose={() => setDlg(null)}
        fields={[{ key: 'name', label: 'Tên', required: true }, { key: 'certificateFile', label: 'File chứng chỉ (.crt/.pem)', required: true }, { key: 'privateKeyFile', label: 'File khoá riêng (.key/.pem)', required: true }]}
        initial={dlg === 'new' ? { name: '', certificateFile: '', privateKeyFile: '' } : { name: dlg.name, certificateFile: dlg.certificateFile, privateKeyFile: dlg.privateKeyFile }}
        onSave={async v => { await ops.saveCert(dlg === 'new' ? null : dlg.id, { name: String(v.name).trim(), certificateFile: String(v.certificateFile).trim(), privateKeyFile: String(v.privateKeyFile).trim() }); reload() }} />}
    </Page>
  )
}

function Dnsbls() {
  const { data, error, reload } = useLoad<Dnsbl[]>(more.dnsbl)
  const [q, setQ] = useState('')
  const [dlg, setDlg] = useState<Dnsbl | 'new' | null>(null)
  const rows = (data ?? []).filter(b => b.host.includes(q.toLowerCase()))
  return (
    <Page title="DNS blacklists" error={error}>
      <p className="mb-4 max-w-2xl text-sm text-muted">Khi IP gửi thư nằm trong một danh sách đang bật, thư được cộng điểm spam. Tổng điểm so với ngưỡng đánh dấu/xoá thư rác trong trang Cài đặt.</p>
      <Head q={q} setQ={setQ} label="Tìm máy chủ DNSBL" addLabel="Thêm DNSBL" onAdd={() => setDlg('new')} />
      <Table heads={['Máy chủ DNSBL', 'Trạng thái', 'Điểm', 'Kết quả mong đợi', 'Thông báo từ chối', '']} empty="Chưa có DNS blacklist nào."
        rows={rows.map(b => [<b key="n">{b.host}</b>, <Status key="s" active={b.active} />, b.score, b.expectedResult || 'bất kỳ', b.rejectMessage,
          <Actions key="a" onEdit={() => setDlg(b)} onDelete={() => confirmDelete(`Xoá ${b.host}?`, () => more.deleteDnsbl(b.id), reload)} />])} />
      {dlg && <FormDialog title={dlg === 'new' ? 'Thêm DNS blacklist' : `Sửa ${dlg.host}`} onClose={() => setDlg(null)}
        fields={[{ key: 'host', label: 'Máy chủ DNSBL', required: true, hint: 'vd. zen.spamhaus.org' },
          { key: 'score', label: 'Điểm cộng khi bị liệt kê', type: 'number' },
          { key: 'expectedResult', label: 'Kết quả mong đợi', hint: 'vd. 127.0.0.2; để trống nếu mọi kết quả đều tính là bị liệt kê' },
          { key: 'rejectMessage', label: 'Thông báo từ chối' }, { key: 'active', label: 'Đang bật', type: 'checkbox' }]}
        initial={dlg === 'new' ? { host: '', score: 5, expectedResult: '127.0.0.2', rejectMessage: 'Rejected by DNS blacklist', active: true }
          : { host: dlg.host, score: dlg.score, expectedResult: dlg.expectedResult, rejectMessage: dlg.rejectMessage, active: dlg.active }}
        onSave={async v => { await more.saveDnsbl(dlg === 'new' ? null : dlg.id, { host: String(v.host), score: Number(v.score), expectedResult: String(v.expectedResult), rejectMessage: String(v.rejectMessage), active: !!v.active }); reload() }} />}
    </Page>
  )
}

function Live() {
  const [running, setRunning] = useState(false)
  const [lines, setLines] = useState<LogLine[]>([])
  const [filter, setFilter] = useState('')
  const [type, setType] = useState('')
  const [follow, setFollow] = useState(true)
  const [err, setErr] = useState('')
  const cur = useRef<{ file: string; offset: number | null }>({ file: '', offset: null })
  const box = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!running) return
    let stop = false, busy = false
    const tick = async () => {
      if (busy) return
      busy = true
      try {
        const r = await more.live(cur.current.file, cur.current.offset)
        if (stop) return
        cur.current = { file: r.file, offset: r.offset }
        if (r.lines.length) setLines(prev => [...prev, ...r.lines].slice(-500))
        setErr(r.notice ?? '')
      } catch (e) { if (!stop) setErr((e as Error).message) }
      finally { busy = false }
    }
    tick()
    const t = setInterval(tick, 2000)
    return () => { stop = true; clearInterval(t) }
  }, [running])
  useEffect(() => { if (follow && box.current) box.current.scrollTop = box.current.scrollHeight }, [lines, follow, filter, type])
  const shown = lines.filter(l => (!type || l.type === type) && (!filter || (l.message + (l.ip ?? '')).toLowerCase().includes(filter.toLowerCase())))
  const types = [...new Set(lines.map(l => l.type))]
  const f = 'rounded-lg border border-line bg-white px-3 py-2 text-sm'
  return (
    <Page title="Live logging">
      <div className="mb-4 flex flex-wrap items-center gap-3">
        <button onClick={() => { if (running) setRunning(false); else { cur.current = { file: '', offset: null }; setRunning(true) } }}
          className="rounded-lg bg-ink px-4 py-2 text-sm font-medium text-white hover:bg-signal">{running ? 'Dừng' : 'Bắt đầu'}</button>
        <input aria-label="Lọc" placeholder="Lọc theo từ khoá hoặc IP" value={filter} onChange={e => setFilter(e.target.value)} className={f} />
        <select aria-label="Loại" value={type} onChange={e => setType(e.target.value)} className={f}>
          <option value="">Mọi loại</option>{types.map(t => <option key={t}>{t}</option>)}
        </select>
        <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={follow} onChange={e => setFollow(e.target.checked)} />Tự cuộn</label>
        <button onClick={() => setLines([])} className="text-sm text-signal hover:underline">Xoá màn hình</button>
        <span role="status" className="text-sm text-muted">{running ? 'Đang theo dõi (2 giây/lần)' : 'Đã dừng'} · {shown.length}/{lines.length} dòng</span>
      </div>
      {err && <p role="alert" className="mb-3 text-sm text-warn">{err}</p>}
      <div ref={box} role="log" aria-label="Log trực tiếp" className="h-[60vh] overflow-auto rounded-xl border border-line bg-white p-3 font-mono text-xs leading-5">
        {shown.length === 0 && <p className="text-muted">{running ? 'Đang chờ dòng log mới…' : 'Nhấn Bắt đầu để xem log trực tiếp.'}</p>}
        {shown.map((l, i) => (
          <div key={i} className={`break-all ${l.type === 'ERROR' ? 'font-medium text-warn' : ''}`}>
            <span className="text-muted">{l.time}</span> <b>{l.type}</b> {l.ip && <span className="text-signal">{l.ip}</span>} {l.message}
          </div>
        ))}
      </div>
      <p className="mt-3 max-w-2xl text-xs text-muted">Đọc trực tiếp từ file log của hôm nay, nên chỉ thấy những loại log đã bật trong Cài đặt → Nhật ký. Giữ tối đa 500 dòng gần nhất trên màn hình.</p>
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

const nav = [['/', 'Tổng quan'], ['/domains', 'Tên miền'], ['/accounts', 'Tài khoản'], ['/aliases', 'Alias'], ['/lists', 'Danh sách phân phối'], ['/ranges', 'IP ranges'], ['/security', 'Chống tấn công'], ['/rules', 'Rules'], ['/queue', 'Hàng đợi gửi'], ['/logs', 'Nhật ký'], ['/settings', 'Cài đặt'], ['/utilities', 'Tiện ích'], ['/diagnostics', 'Chẩn đoán'], ['/ssl', 'Chứng chỉ SSL'], ['/dnsbl', 'DNS blacklists'], ['/live', 'Live logging']]

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
          <Route path="lists" element={<Lists />} />
          <Route path="ranges" element={<Ranges />} />
          <Route path="security" element={<Security />} />
          <Route path="queue" element={<Queue />} />
          <Route path="logs" element={<Logs />} />
          <Route path="rules" element={<Rules />} />
          <Route path="settings" element={<Settings />} />
          <Route path="utilities" element={<Utilities />} />
          <Route path="diagnostics" element={<Diagnostics />} />
          <Route path="ssl" element={<Ssl />} />
          <Route path="dnsbl" element={<Dnsbls />} />
          <Route path="live" element={<Live />} />
        </Routes>
      </main>
    </div>
  )
}

export default function App() {
  return <Routes><Route path="/login" element={<Login />} /><Route path="/*" element={<Shell />} /></Routes>
}
