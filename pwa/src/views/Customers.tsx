import { ChevronLeft, ChevronRight, Download, FileSpreadsheet, FileUp, Plus, Search, Trash2 } from "lucide-react";
import { useMemo, useRef, useState } from "react";
import { uid } from "../domain";
import { keyForHeader, readWorkbook, rowsToLeads, type ParsedImport } from "../importers";
import { useStore } from "../store";
import type { Lead } from "../types";
import { Button, EmptyState, Field, GradeBadge, Modal, PageHeader } from "../components/ui";

const emptyLead = (): Lead => ({
  id: uid(), buyerId: "", name: "", nickname: "", phone: "", email: "", company: "", country: "",
  productInterest: "", stage: "新客户", grade: "D", score: 0, owner: "", tags: [], notes: "", source: "PWA 人工创建",
  updatedAt: new Date().toISOString(), customFields: {}
});

const coreLabels: Record<string, string> = {
  buyerId: "Buyer ID",
  name: "客户名称",
  nickname: "昵称",
  phone: "电话",
  email: "邮箱",
  company: "公司",
  country: "国家 / 地区",
  productInterest: "产品兴趣",
  stage: "跟进阶段",
  owner: "负责人",
  tags: "标签",
  notes: "备注",
  source: "来源"
};

const pageSizes = [10, 30] as const;
type PageSize = typeof pageSizes[number];

export function Customers() {
  const { leads, saveLead, importLeads, removeLead, loadDemo } = useStore();
  const [query, setQuery] = useState("");
  const [editing, setEditing] = useState<Lead | null>(null);
  const [notice, setNotice] = useState("");
  const [reading, setReading] = useState(false);
  const [pending, setPending] = useState<ParsedImport | null>(null);
  const [selectedSheetName, setSelectedSheetName] = useState("");
  const [pageSize, setPageSize] = useState<PageSize>(10);
  const [page, setPage] = useState(1);
  const fileRef = useRef<HTMLInputElement>(null);
  const filtered = useMemo(() => leads.filter(lead => JSON.stringify(lead).toLowerCase().includes(query.toLowerCase())), [leads, query]);
  const totalPages = Math.max(1, Math.ceil(filtered.length / pageSize));
  const currentPage = Math.min(page, totalPages);
  const pageLeads = useMemo(
    () => filtered.slice((currentPage - 1) * pageSize, currentPage * pageSize),
    [currentPage, filtered, pageSize]
  );
  const rangeStart = filtered.length ? (currentPage - 1) * pageSize + 1 : 0;
  const rangeEnd = Math.min(currentPage * pageSize, filtered.length);

  const prepareImport = async (file?: File) => {
    if (!file) return;
    setReading(true);
    setNotice("");
    try {
      const parsed = await readWorkbook(file);
      setPending(parsed);
      setSelectedSheetName(parsed.preferredSheetName);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "文件读取失败");
    } finally {
      setReading(false);
    }
  };

  const confirmImport = async () => {
    if (!pending) return;
    const sheet = pending.sheets.find(item => item.name === selectedSheetName);
    if (!sheet) return;
    const result = rowsToLeads(sheet.rows, leads, { fileName: pending.fileName, sheetName: sheet.name });
    await importLeads(result.changedLeads, result.removedPlaceholderIds);
    const cleanup = result.removedPlaceholderIds.length
      ? `；已清理 ${result.removedPlaceholderIds.length} 条旧版错误占位记录`
      : "";
    setNotice(`已从“${sheet.name}”导入 ${result.total} 行：新增 ${result.created} 位，更新 ${result.updated} 位客户${cleanup}。`);
    setPage(1);
    setPending(null);
  };

  const exportData = () => {
    const blob = new Blob([JSON.stringify(leads, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob); const a = document.createElement("a");
    a.href = url; a.download = `ai-sales-os-customers-${new Date().toISOString().slice(0, 10)}.json`; a.click(); URL.revokeObjectURL(url);
  };
  return <>
    <PageHeader title="客户列表" subtitle="Buyer ID 优先识别同一客户；资料只保存在当前浏览器。"
      actions={<><Button variant="secondary" onClick={exportData}><Download size={16}/>导出</Button><Button variant="secondary" disabled={reading} onClick={() => fileRef.current?.click()}><FileUp size={16}/>{reading ? "正在读取…" : "导入 Excel / CSV"}</Button><Button onClick={() => setEditing(emptyLead())}><Plus size={16}/>新建客户</Button></>}/>
    <input ref={fileRef} hidden type="file" aria-label="导入客户文件" accept=".xlsx,.xls,.csv" onChange={event => {
      void prepareImport(event.target.files?.[0]);
      event.currentTarget.value = "";
    }}/>
    {notice && <div className="notice" role="status">{notice}<button aria-label="关闭提示" onClick={() => setNotice("")}>×</button></div>}
    <div className="toolbar"><div className="search"><Search/><input aria-label="搜索客户" name="customer-search" autoComplete="off" value={query} onChange={event => { setQuery(event.target.value); setPage(1); }} placeholder="搜索姓名、Buyer ID、电话、邮箱或自定义字段…"/></div><span>{filtered.length} 位客户</span></div>
    <section className="table-shell">
      {!leads.length ? <EmptyState title="建立统一客户档案" body="支持 Excel / CSV 动态字段导入；也可以先加载示例数据体验。" action={<Button variant="secondary" onClick={loadDemo}>加载示例数据</Button>}/> :
      <div className="data-table">
        <div className="table-row table-head"><span>客户</span><span>统一身份</span><span>公司 / 产品</span><span>阶段</span><span>AI 等级</span><span/></div>
        {pageLeads.map(lead => <div className="table-row" role="button" tabIndex={0} key={lead.id}
          onClick={() => setEditing({ ...lead })}
          onKeyDown={event => {
            if (event.key === "Enter" || event.key === " ") {
              event.preventDefault();
              setEditing({ ...lead });
            }
          }}>
          <span className="customer-cell"><strong>{lead.nickname || lead.name}</strong><small>{lead.email || lead.phone || "联系方式待补充"}</small></span>
          <span><strong>{lead.buyerId || "电话号码兜底"}</strong><small>{lead.buyerId ? "Buyer ID" : lead.phone}</small></span>
          <span><strong>{lead.company || "—"}</strong><small>{lead.productInterest || "产品待补充"}</small></span>
          <span>{lead.stage}</span><span><GradeBadge grade={lead.grade} score={lead.score}/></span>
          <span><button className="row-delete" aria-label={`删除 ${lead.name}`} onClick={event => { event.stopPropagation(); if (confirm(`删除 ${lead.name}？`)) void removeLead(lead.id); }}><Trash2 size={15}/></button></span>
        </div>)}
        {!pageLeads.length && <div className="table-empty">没有找到匹配的客户</div>}
      </div>}
    </section>
    {leads.length > 0 && <nav className="pagination" aria-label="客户列表分页">
      <label className="page-size-control">
        <span>每页显示</span>
        <select aria-label="每页客户数" name="customer-page-size" value={pageSize} onChange={event => { setPageSize(Number(event.target.value) as PageSize); setPage(1); }}>
          {pageSizes.map(size => <option key={size} value={size}>{size} 位</option>)}
        </select>
      </label>
      <span className="pagination-summary">第 {rangeStart}–{rangeEnd} 条，共 {filtered.length} 位客户</span>
      <div className="pager">
        <button aria-label="上一页" disabled={currentPage === 1} onClick={() => setPage(current => Math.max(1, current - 1))}><ChevronLeft/></button>
        <span>第 <strong>{currentPage}</strong> / {totalPages} 页</span>
        <button aria-label="下一页" disabled={currentPage === totalPages} onClick={() => setPage(current => Math.min(totalPages, current + 1))}><ChevronRight/></button>
      </div>
    </nav>}
    {pending && <ImportPreview parsed={pending} sheetName={selectedSheetName} onSheetChange={setSelectedSheetName} onClose={() => setPending(null)} onConfirm={() => void confirmImport()}/>}
    {editing && <LeadEditor lead={editing} onClose={() => setEditing(null)} onSave={async lead => { await saveLead({ ...lead, updatedAt: new Date().toISOString() }); setEditing(null); }}/>}
  </>;
}

function ImportPreview({ parsed, sheetName, onSheetChange, onClose, onConfirm }: {
  parsed: ParsedImport;
  sheetName: string;
  onSheetChange: (value: string) => void;
  onClose: () => void;
  onConfirm: () => void;
}) {
  const sheet = parsed.sheets.find(item => item.name === sheetName) || parsed.sheets[0];
  const { mappedHeaders, preview } = useMemo(() => ({
    mappedHeaders: sheet.headers
      .map(header => ({ header, key: keyForHeader(header) }))
      .filter((item, index, items) => item.key && items.findIndex(candidate => candidate.key === item.key) === index),
    preview: rowsToLeads(
      sheet.rows.slice(0, 5),
      [],
      { fileName: parsed.fileName, sheetName: sheet.name }
    ).changedLeads
  }), [parsed.fileName, sheet]);
  return <Modal title="确认客户导入" onClose={onClose} wide>
    <div className="import-file-summary">
      <span><FileSpreadsheet/></span>
      <div><strong>{parsed.fileName}</strong><small>已读取 {parsed.sheets.length} 个非空工作表；默认选择文件保存时的活动表。</small></div>
    </div>
    <div className="form-grid import-controls">
      <Field label="导入工作表">
        <select name="import-sheet" value={sheet.name} onChange={event => onSheetChange(event.target.value)}>
          {parsed.sheets.map(item => <option key={item.name} value={item.name}>{item.name}（{item.rows.length} 行）</option>)}
        </select>
      </Field>
      <div className="import-shape"><span>将导入</span><strong>{sheet.rows.length}</strong><small>位客户 · {sheet.headers.length} 个原始字段</small></div>
    </div>
    <div className="mapped-fields">
      <strong>已识别核心字段</strong>
      <div>{mappedHeaders.map(item => <span key={item.header}>{coreLabels[item.key!] || item.key}</span>)}</div>
    </div>
    <div className="import-preview">
      <div className="import-preview-head"><span>客户</span><span>Buyer ID</span><span>电话 / 邮箱</span><span>负责人 / 阶段</span></div>
      {preview.map(lead => <div key={lead.id}>
        <span><strong>{lead.nickname || lead.name}</strong><small>{lead.country || "地区待补充"}</small></span>
        <span>{lead.buyerId || "—"}</span>
        <span><strong>{lead.phone || "—"}</strong><small>{lead.email || "邮箱待补充"}</small></span>
        <span><strong>{lead.owner || "—"}</strong><small>{lead.stage}</small></span>
      </div>)}
    </div>
    <p className="import-note">所有原始列都会保存在客户自定义字段中；Buyer ID 存在时优先更新同一客户，缺失时才使用电话匹配。</p>
    <div className="modal-actions"><Button variant="secondary" onClick={onClose}>取消</Button><Button onClick={onConfirm}>导入 {sheet.rows.length} 位客户</Button></div>
  </Modal>;
}

function LeadEditor({ lead, onClose, onSave }: { lead: Lead; onClose: () => void; onSave: (lead: Lead) => Promise<void> }) {
  const [value, setValue] = useState(lead);
  const set = (key: keyof Lead, next: string) => setValue(current => ({ ...current, [key]: next }));
  return <Modal title={lead.name ? "编辑客户" : "新建客户"} onClose={onClose} wide>
    <div className="form-grid">
      <Field label="Buyer ID" hint="存在时作为跨板块统一身份"><input name="buyer-id" autoComplete="off" spellCheck={false} value={value.buyerId} onChange={e => set("buyerId", e.target.value)}/></Field>
      <Field label="客户名称"><input name="customer-name" autoComplete="name" value={value.name} onChange={e => set("name", e.target.value)}/></Field>
      <Field label="Nickname"><input name="customer-nickname" autoComplete="off" value={value.nickname} onChange={e => set("nickname", e.target.value)}/></Field>
      <Field label="WhatsApp / 电话"><input type="tel" name="customer-phone" autoComplete="tel" value={value.phone} onChange={e => set("phone", e.target.value)}/></Field>
      <Field label="邮箱"><input type="email" name="customer-email" autoComplete="email" spellCheck={false} value={value.email} onChange={e => set("email", e.target.value)}/></Field>
      <Field label="公司"><input name="customer-company" autoComplete="organization" value={value.company} onChange={e => set("company", e.target.value)}/></Field>
      <Field label="国家 / 地区"><input name="customer-country" autoComplete="country-name" value={value.country} onChange={e => set("country", e.target.value)}/></Field>
      <Field label="关注产品"><input name="product-interest" autoComplete="off" value={value.productInterest} onChange={e => set("productInterest", e.target.value)}/></Field>
      <Field label="销售阶段"><select name="sales-stage" value={value.stage} onChange={e => set("stage", e.target.value)}>{["新客户","初步沟通","需求确认","报价中","谈判中","成交","复购","暂停"].map(x => <option key={x}>{x}</option>)}</select></Field>
      <Field label="负责人"><input name="lead-owner" autoComplete="off" value={value.owner} onChange={e => set("owner", e.target.value)}/></Field>
      <Field label="备注"><textarea name="lead-notes" autoComplete="off" value={value.notes} onChange={e => set("notes", e.target.value)} rows={4}/></Field>
    </div>
    <div className="modal-actions"><Button variant="secondary" onClick={onClose}>取消</Button><Button disabled={!value.name.trim()} onClick={() => void onSave(value)}>保存客户</Button></div>
  </Modal>;
}
