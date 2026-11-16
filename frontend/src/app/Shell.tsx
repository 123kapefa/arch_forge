import { NavLink, Outlet } from 'react-router';
import { SCREENS } from '../shared/lib/screens';
import styles from './Shell.module.css';

/** Глобальная оболочка: верхняя панель (проект + навигация) и область экрана. */
export function Shell() {
  return (
    <div className={styles.shell}>
      <header className={styles.topbar}>
        <span className={styles.project}><span className={styles.dot} aria-hidden="true" />arch_forge</span>
        <nav aria-label="Экраны" className={styles.nav}>
          {SCREENS.filter((s) => s.nav).map((s) => (
            <NavLink key={s.path} to={s.path} end className={({ isActive }) => (isActive ? styles.active : styles.link)}>
              {s.title}
            </NavLink>
          ))}
        </nav>
      </header>
      <main className={styles.main}>
        <Outlet />
      </main>
    </div>
  );
}
