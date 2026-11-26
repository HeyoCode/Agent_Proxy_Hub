import React, { useLayoutEffect, useRef, useState } from 'react';

export interface SegTabItem<T extends string = string> {
  value: T;
  label: string;
  count?: number | string;
}

interface SegmentedTabsProps<T extends string> {
  items: SegTabItem<T>[];
  value: T;
  onChange: (value: T) => void;
  size?: 'md' | 'sm';
  className?: string;
}

export function SegmentedTabs<T extends string>({
  items,
  value,
  onChange,
  size = 'md',
  className = ''
}: SegmentedTabsProps<T>) {
  const containerRef = useRef<HTMLDivElement>(null);
  const btnRefs = useRef<Record<string, HTMLButtonElement | null>>({});
  const [indicator, setIndicator] = useState<{ left: number; width: number; ready: boolean }>({
    left: 0,
    width: 0,
    ready: false
  });
  const mountedRef = useRef(false);

  const measure = () => {
    const container = containerRef.current;
    const activeBtn = btnRefs.current[value];
    if (!container || !activeBtn) return;
    const left = activeBtn.offsetLeft;
    const width = activeBtn.offsetWidth;
    setIndicator({ left, width, ready: mountedRef.current });
    mountedRef.current = true;
  };

  useLayoutEffect(() => {
    measure();
    const ro = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(measure) : null;
    if (ro && containerRef.current) ro.observe(containerRef.current);
    return () => ro?.disconnect();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value, items.length, JSON.stringify(items.map(i => `${i.value}:${i.label}:${i.count ?? ''}`))]);

  return (
    <div ref={containerRef} className={`seg-pill-group seg-${size} ${className}`.trim()} role="tablist">
      <div
        className={`seg-sliding-thumb ${indicator.ready ? 'animate' : ''}`}
        style={{
          width: indicator.width,
          transform: `translate3d(${indicator.left - 3}px, 0, 0)`
        }}
        aria-hidden="true"
      />
      {items.map(item => {
        const isActive = item.value === value;
        return (
          <button
            key={item.value}
            ref={el => { btnRefs.current[item.value] = el; }}
            type="button"
            role="tab"
            aria-selected={isActive}
            className={`seg-pill-btn ${isActive ? 'active' : ''}`}
            onClick={() => onChange(item.value)}
          >
            <span>{item.label}</span>
            {item.count !== undefined && (
              <span className="seg-count num">{item.count}</span>
            )}
          </button>
        );
      })}
    </div>
  );
}
