import {
  BarChart3, BookOpenText, Bot, BrainCircuit, ChevronRight, ContactRound, Download,
  LayoutDashboard, Mail, Megaphone, Menu, Moon, Settings, Sun, WifiOff, X
} from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";

export type PageKey = "dashboard" | "intelligence" | "customers" | "whatsapp" | "email" | "campaigns" | "knowledge" | "analytics" | "settings";

const nav: { key: PageKey; label: string; icon: typeof LayoutDashboard }[] = [
  { key: "dashboard", label: "看板", icon: LayoutDashboard },
  { key: "customers", label: "客户列表", icon: ContactRound },
  { key: "intelligence", label: "商机智能", icon: BrainCircuit },
  { key: "whatsapp", label: "WhatsApp", icon: Bot },
  { key: "email", label: "邮件箱", icon: Mail },
  { key: "campaigns", label: "自动化触达", icon: Megaphone },
  { key: "knowledge", label: "知识库", icon: BookOpenText },
  { key: "analytics", label: "客户智能分析", icon: BarChart3 },
  { key: "settings", label: "API 与数据设置", icon: Settings }
];

export function Shell({ page, setPage, children, onInstall, canInstall }: {
  page: PageKey; setPage: (page: PageKey) => void; children: ReactNode; onInstall: () => void; canInstall: boolean;
}) {
  const [mobileOpen, setMobileOpen] = useState(false);
  const [dark, setDark] = useState(() => localStorage.getItem("ai-sales-os-theme") === "dark");
  useEffect(() => {
    document.documentElement.dataset.theme = dark ? "dark" : "light";
    localStorage.setItem("ai-sales-os-theme", dark ? "dark" : "light");
  }, [dark]);
  const choose = (key: PageKey) => { setPage(key); setMobileOpen(false); };
  return <div className="app-shell">
    <aside id="primary-navigation" className={`sidebar ${mobileOpen ? "open" : ""}`}>
      <div className="brand"><img src={`${import.meta.env.BASE_URL}pwa-192.png`} alt="" width={52} height={52}/><div><strong>AI Sales OS</strong><span>PWA v5.5.3 · LOCAL FIRST</span></div><button className="mobile-close" onClick={() => setMobileOpen(false)} aria-label="关闭导航"><X/></button></div>
      <nav>
        <span className="nav-group">COMMAND CENTER</span>
        {nav.slice(0, 3).map(item => <NavItem key={item.key} label={item.label} icon={item.icon} active={page === item.key} onClick={() => choose(item.key)}/>)}
        <span className="nav-group">CUSTOMER OPERATIONS</span>
        {nav.slice(3, 6).map(item => <NavItem key={item.key} label={item.label} icon={item.icon} active={page === item.key} onClick={() => choose(item.key)}/>)}
        <span className="nav-group">INSIGHTS</span>
        {nav.slice(6).map(item => <NavItem key={item.key} label={item.label} icon={item.icon} active={page === item.key} onClick={() => choose(item.key)}/>)}
      </nav>
      <div className="sidebar-note"><WifiOff size={17}/><div><strong>纯 PWA 模式</strong><span>不伪装后台常驻连接</span></div></div>
    </aside>
    {mobileOpen && <button className="nav-scrim" onClick={() => setMobileOpen(false)} aria-label="关闭导航"/>}
    <main className="main">
      <header className="topbar">
        <button className="menu-button" onClick={() => setMobileOpen(true)} aria-label="打开导航" aria-controls="primary-navigation" aria-expanded={mobileOpen}><Menu/></button>
        <div className="top-title"><strong>{nav.find(x => x.key === page)?.label}</strong><span>客户资料保存在当前浏览器</span></div>
        <div className="top-actions">
          <span className="local-status"><span/>本机数据</span>
          {canInstall && <button className="button secondary install-button" onClick={onInstall}><Download size={16}/>安装 PWA</button>}
          <button className="icon-button theme-button" onClick={() => setDark(!dark)} aria-label="切换主题">{dark ? <Sun size={18}/> : <Moon size={18}/>}</button>
        </div>
      </header>
      <div className="content">{children}</div>
    </main>
    <nav className="bottom-nav">
      {nav.slice(0, 5).map(item => {
        const Icon = item.icon;
        return <button key={item.key} className={page === item.key ? "active" : ""} onClick={() => choose(item.key)}><Icon/><span>{item.label.replace(" Inbox", "")}</span></button>;
      })}
      <button onClick={() => setMobileOpen(true)}><Menu/><span>更多</span></button>
    </nav>
  </div>;
}

function NavItem({ label, icon: Icon, active, onClick }: { label: string; icon: typeof LayoutDashboard; active: boolean; onClick: () => void }) {
  return <button className={`nav-item ${active ? "active" : ""}`} onClick={onClick} aria-current={active ? "page" : undefined}><Icon size={18}/><span>{label}</span>{active && <ChevronRight size={15}/>}</button>;
}
