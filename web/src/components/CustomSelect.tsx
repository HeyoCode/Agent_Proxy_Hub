import React, { useEffect, useLayoutEffect, useRef, useState } from 'react';
import ReactDOM from 'react-dom';
import { CheckTinyIcon, ChevronDownIcon } from './Icons';

export interface SelectOption {
  value: string;
  label: string;
  sublabel?: string;
  tag?: string;
  dotColor?: string;
}

interface CustomSelectProps {
  value: string;
  options: SelectOption[];
  onChange: (value: string) => void;
  variant?: 'light' | 'midnight';
  size?: 'md' | 'sm';
  prefixIcon?: React.ReactNode;
  placeholder?: string;
  ariaLabel?: string;
  className?: string;
  style?: React.CSSProperties;
  minMenuWidth?: number;
}

export const CustomSelect: React.FC<CustomSelectProps> = ({
  value,
  options,
  onChange,
  variant = 'light',
  size = 'md',
  prefixIcon,
  placeholder = '请选择',
  ariaLabel,
  className = '',
  style,
  minMenuWidth
}) => {
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(-1);
  const [menuPos, setMenuPos] = useState<{
    left: number;
    top?: number;
    bottom?: number;
    width: number;
    placement: 'bottom' | 'top';
  } | null>(null);

  const triggerRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);

  const selected = options.find(o => o.value === value) ?? options[0];

  const updatePosition = () => {
    const el = triggerRef.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    const menuWidth = Math.max(rect.width, minMenuWidth ?? 0);
    const estHeight = Math.min(264, options.length * 40 + 16);
    const spaceBelow = window.innerHeight - rect.bottom - 12;
    const spaceAbove = rect.top - 12;
    const openUp = spaceBelow < estHeight && spaceAbove > spaceBelow;
    const left = Math.max(10, Math.min(rect.left, window.innerWidth - menuWidth - 10));

    if (openUp) {
      setMenuPos({
        left,
        bottom: window.innerHeight - rect.top + 6,
        width: menuWidth,
        placement: 'top'
      });
    } else {
      setMenuPos({
        left,
        top: rect.bottom + 6,
        width: menuWidth,
        placement: 'bottom'
      });
    }
  };

  useLayoutEffect(() => {
    if (open) {
      updatePosition();
      const curIdx = options.findIndex(o => o.value === value);
      setActiveIndex(curIdx >= 0 ? curIdx : 0);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, options.length, value]);

  useEffect(() => {
    if (!open) return;

    const handlePointerDown = (e: MouseEvent) => {
      const t = e.target as Node;
      if (triggerRef.current?.contains(t) || menuRef.current?.contains(t)) {
        return;
      }
      setOpen(false);
    };

    const handleScrollOrResize = (e: Event) => {
      if (menuRef.current && e.target instanceof Node && menuRef.current.contains(e.target)) {
        return;
      }
      setOpen(false);
    };

    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        setOpen(false);
        triggerRef.current?.focus();
      } else if (e.key === 'ArrowDown') {
        e.preventDefault();
        setActiveIndex(i => (options.length ? (i + 1) % options.length : -1));
      } else if (e.key === 'ArrowUp') {
        e.preventDefault();
        setActiveIndex(i => (options.length ? (i - 1 + options.length) % options.length : -1));
      } else if (e.key === 'Enter' && activeIndex >= 0 && options[activeIndex]) {
        e.preventDefault();
        onChange(options[activeIndex].value);
        setOpen(false);
        triggerRef.current?.focus();
      }
    };

    document.addEventListener('mousedown', handlePointerDown);
    window.addEventListener('resize', handleScrollOrResize);
    window.addEventListener('scroll', handleScrollOrResize, true);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('mousedown', handlePointerDown);
      window.removeEventListener('resize', handleScrollOrResize);
      window.removeEventListener('scroll', handleScrollOrResize, true);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [open, options, activeIndex, onChange]);

  const menuNode = open && menuPos
    ? ReactDOM.createPortal(
      <div
        ref={menuRef}
        role="listbox"
        aria-label={ariaLabel}
        className={`cselect-menu cselect-menu-${variant} cselect-place-${menuPos.placement}`}
        style={{
          left: menuPos.left,
          top: menuPos.top,
          bottom: menuPos.bottom,
          width: menuPos.width
        }}
      >
        {options.map((opt, idx) => {
          const isSelected = opt.value === value;
          const isHighlighted = idx === activeIndex;
          return (
            <button
              key={opt.value}
              type="button"
              role="option"
              aria-selected={isSelected}
              className={`cselect-opt ${isSelected ? 'selected' : ''} ${isHighlighted ? 'highlighted' : ''}`}
              onMouseEnter={() => setActiveIndex(idx)}
              onClick={() => {
                onChange(opt.value);
                setOpen(false);
                triggerRef.current?.focus();
              }}
            >
              <span className="cselect-opt-main">
                {opt.dotColor && (
                  <span className="cselect-dot" style={{ backgroundColor: opt.dotColor }} />
                )}
                <span className="cselect-opt-text">
                  <span className="cselect-opt-label">{opt.label}</span>
                  {opt.sublabel && <span className="cselect-opt-sub">{opt.sublabel}</span>}
                </span>
              </span>
              <span className="cselect-opt-right">
                {opt.tag && <span className="cselect-opt-tag mono">{opt.tag}</span>}
                <span className="cselect-opt-check">{isSelected ? <CheckTinyIcon size={12} /> : null}</span>
              </span>
            </button>
          );
        })}
      </div>,
      document.body
    )
    : null;

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        role="combobox"
        aria-expanded={open}
        aria-haspopup="listbox"
        aria-label={ariaLabel}
        className={`cselect-trigger cselect-${variant} cselect-${size} ${open ? 'open' : ''} ${className}`.trim()}
        style={style}
        onClick={() => setOpen(v => !v)}
        onKeyDown={e => {
          if (!open && (e.key === 'ArrowDown' || e.key === 'ArrowUp' || e.key === 'Enter' || e.key === ' ')) {
            e.preventDefault();
            setOpen(true);
          }
        }}
      >
        <span className="cselect-value-wrap">
          {prefixIcon ? <span className="cselect-prefix-icon">{prefixIcon}</span> : null}
          {!prefixIcon && selected?.dotColor ? (
            <span className="cselect-dot" style={{ backgroundColor: selected.dotColor }} />
          ) : null}
          <span className="cselect-value-text">{selected?.label ?? placeholder}</span>
        </span>
        <ChevronDownIcon size={13} className={`cselect-chevron ${open ? 'open' : ''}`} />
      </button>
      {menuNode}
    </>
  );
};
