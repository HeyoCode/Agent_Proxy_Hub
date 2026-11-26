import React from 'react';

interface IconProps {
  size?: number;
  className?: string;
}

export const BrandLogoIcon: React.FC<IconProps> = ({ size = 24, className }) => (
  <svg width={size} height={size} viewBox="0 0 28 28" fill="none" className={className} aria-hidden="true">
    <path
      d="M14 3L5.2 6.9v6.2c0 5.5 3.7 10.4 8.8 11.9 5.1-1.5 8.8-6.4 8.8-11.9V6.9L14 3Z"
      stroke="currentColor"
      strokeWidth="1.85"
      strokeLinejoin="round"
    />
    <path d="M9.6 14h8.8M14 9.6v8.8" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" opacity="0.45" />
    <circle cx="14" cy="14" r="2.8" fill="#4A56E2" />
  </svg>
);

export const OverviewNavIcon: React.FC<IconProps> = ({ size = 19, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className} aria-hidden="true">
    <rect x="3.5" y="3.5" width="7" height="9" rx="2" stroke="currentColor" strokeWidth="1.75" />
    <rect x="13.5" y="3.5" width="7" height="6" rx="2" stroke="currentColor" strokeWidth="1.75" />
    <rect x="3.5" y="15.5" width="7" height="5" rx="2" stroke="currentColor" strokeWidth="1.75" />
    <rect x="13.5" y="12.5" width="7" height="8" rx="2" stroke="currentColor" strokeWidth="1.75" />
  </svg>
);

export const UsageNavIcon: React.FC<IconProps> = ({ size = 19, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className} aria-hidden="true">
    <path d="M4 18.5V11M9.5 18.5V6.5M15 18.5v-5M20 18.5V9" stroke="currentColor" strokeWidth="1.85" strokeLinecap="round" />
  </svg>
);

export const NodesNavIcon: React.FC<IconProps> = ({ size = 19, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className} aria-hidden="true">
    <circle cx="12" cy="12" r="8.5" stroke="currentColor" strokeWidth="1.75" />
    <ellipse cx="12" cy="12" rx="3.8" ry="8.5" stroke="currentColor" strokeWidth="1.5" />
    <path d="M4 12h16" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
  </svg>
);

export const AppsNavIcon: React.FC<IconProps> = ({ size = 19, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className} aria-hidden="true">
    <rect x="3.5" y="4.5" width="17" height="15" rx="3" stroke="currentColor" strokeWidth="1.75" />
    <path d="M8 10l2.8 2.2L8 14.4M12.5 14.5H16" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round" />
  </svg>
);

export const IsolationNavIcon: React.FC<IconProps> = ({ size = 19, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className} aria-hidden="true">
    <path d="M12 3.5L5.5 6.4v4.9c0 4.3 2.8 8.1 6.5 9.2 3.7-1.1 6.5-4.9 6.5-9.2V6.4L12 3.5Z" stroke="currentColor" strokeWidth="1.75" strokeLinejoin="round" />
    <path d="M9.3 12l2 2 3.5-3.6" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round" />
  </svg>
);

export const AlertCircleIcon: React.FC<IconProps> = ({ size = 17, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className} aria-hidden="true">
    <circle cx="12" cy="12" r="9" stroke="currentColor" strokeWidth="1.8" />
    <path d="M12 8v4.8" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" />
    <circle cx="12" cy="16.2" r="1.1" fill="currentColor" />
  </svg>
);

export const ShieldCheckIcon: React.FC<IconProps> = ({ size = 17, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className} aria-hidden="true">
    <path d="M12 3.5L5.5 6.4v4.9c0 4.3 2.8 8.1 6.5 9.2 3.7-1.1 6.5-4.9 6.5-9.2V6.4L12 3.5Z" stroke="currentColor" strokeWidth="1.8" />
    <path d="M9.2 12.1l2 2 3.7-3.8" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
  </svg>
);

export const SunIcon: React.FC<IconProps> = ({ size = 17, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className} aria-hidden="true">
    <circle cx="12" cy="12" r="4" stroke="currentColor" strokeWidth="1.75" />
    <path d="M12 3v2M12 19v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M3 12h2M19 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" />
  </svg>
);

export const MoonIcon: React.FC<IconProps> = ({ size = 17, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className} aria-hidden="true">
    <path d="M19.5 14.5A7.5 7.5 0 0 1 9.5 4.5a7.5 7.5 0 1 0 10 10Z" stroke="currentColor" strokeWidth="1.75" strokeLinejoin="round" />
  </svg>
);

export const GitHubIcon: React.FC<IconProps> = ({ size = 17, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="currentColor" className={className} aria-hidden="true">
    <path d="M12 .5C5.65.5.5 5.65.5 12c0 5.08 3.29 9.39 7.86 10.91.57.1.78-.25.78-.55 0-.27-.01-1.17-.02-2.12-3.2.7-3.88-1.36-3.88-1.36-.52-1.33-1.28-1.68-1.28-1.68-1.05-.72.08-.7.08-.7 1.16.08 1.77 1.19 1.77 1.19 1.03 1.77 2.7 1.26 3.36.96.1-.75.4-1.26.73-1.55-2.55-.29-5.24-1.28-5.24-5.68 0-1.26.45-2.28 1.19-3.08-.12-.29-.52-1.46.11-3.05 0 0 .97-.31 3.18 1.18a11.1 11.1 0 0 1 5.79 0c2.21-1.49 3.18-1.18 3.18-1.18.63 1.59.23 2.76.11 3.05.74.8 1.19 1.82 1.19 3.08 0 4.41-2.69 5.39-5.25 5.67.41.36.78 1.06.78 2.14 0 1.54-.01 2.79-.01 3.17 0 .31.21.66.79.55A11.51 11.51 0 0 0 23.5 12C23.5 5.65 18.35.5 12 .5Z" />
  </svg>
);

export const SeatBrandIcon: React.FC<{ seatId: string; size?: number; className?: string }> = ({ seatId, size = 18, className }) => {
  if (seatId === 'claude') {
    return (
      <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className} aria-hidden="true">
        <path d="M12 3.5v17M4.6 7.8l14.8 8.4M19.4 7.8L4.6 16.2" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
        <circle cx="12" cy="12" r="2.2" fill="currentColor" />
      </svg>
    );
  }
  if (seatId === 'chatgpt') {
    return (
      <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className} aria-hidden="true">
        <circle cx="12" cy="12" r="8" stroke="currentColor" strokeWidth="1.75" />
        <path d="M8.5 10.5c1-2.2 4.5-2.6 6-.7 1.4 1.8.4 4.8-2 5.4-1.8.5-3.6-.5-4-2.1" stroke="currentColor" strokeWidth="1.65" strokeLinecap="round" />
      </svg>
    );
  }
  if (seatId === 'agy' || seatId === 'antigravity') {
    return (
      <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className} aria-hidden="true">
        <path d="M12 3.5L19 18.8l-7-3.2-7 3.2L12 3.5Z" stroke="currentColor" strokeWidth="1.75" strokeLinejoin="round" />
        <path d="M12 3.5v12.1" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" opacity="0.55" />
      </svg>
    );
  }
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className} aria-hidden="true">
      <path d="M5 4l13.5 5.8-5.7 2.4L10.4 18 5 4Z" stroke="currentColor" strokeWidth="1.75" strokeLinejoin="round" />
      <path d="M13 12.2l5 5" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" />
    </svg>
  );
};

export const CheckTinyIcon: React.FC<{ size?: number }> = ({ size = 11 }) => (
  <svg width={size} height={size} viewBox="0 0 16 16" fill="none" aria-hidden="true">
    <path d="M3.5 8.2l2.8 2.8 6.2-6.2" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
  </svg>
);

export const CrossTinyIcon: React.FC<{ size?: number }> = ({ size = 11 }) => (
  <svg width={size} height={size} viewBox="0 0 16 16" fill="none" aria-hidden="true">
    <path d="M4.5 4.5l7 7M11.5 4.5l-7 7" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
  </svg>
);

export const ChevronDownIcon: React.FC<IconProps> = ({ size = 14, className }) => (
  <svg width={size} height={size} viewBox="0 0 16 16" fill="none" className={className} aria-hidden="true">
    <path d="M4.2 6.2L8 10l3.8-3.8" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" />
  </svg>
);

export const RefreshSpinIcon: React.FC<IconProps & { spinning?: boolean }> = ({ size = 13, className, spinning }) => (
  <svg
    width={size}
    height={size}
    viewBox="0 0 18 18"
    fill="none"
    className={`${spinning ? 'icon-spin' : ''} ${className ?? ''}`.trim()}
    aria-hidden="true"
  >
    <path d="M14.8 9A5.8 5.8 0 1 1 13.1 4.9" stroke="currentColor" strokeWidth="1.65" strokeLinecap="round" />
    <path d="M14.9 2.9v2.8h-2.8" stroke="currentColor" strokeWidth="1.65" strokeLinecap="round" strokeLinejoin="round" />
  </svg>
);

export const CopyIcon: React.FC<IconProps> = ({ size = 13, className }) => (
  <svg width={size} height={size} viewBox="0 0 18 18" fill="none" className={className} aria-hidden="true">
    <rect x="6.2" y="6.2" width="8.6" height="8.6" rx="2" stroke="currentColor" strokeWidth="1.6" />
    <path d="M4.3 11.4a1.8 1.8 0 0 1-1.1-1.65V4.8a1.6 1.6 0 0 1 1.6-1.6h4.95c.72 0 1.35.46 1.56 1.1" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
  </svg>
);

export const RadarVerifyIcon: React.FC<IconProps> = ({ size = 13, className }) => (
  <svg width={size} height={size} viewBox="0 0 18 18" fill="none" className={className} aria-hidden="true">
    <circle cx="9" cy="9" r="6.2" stroke="currentColor" strokeWidth="1.6" />
    <circle cx="9" cy="9" r="2.2" fill="currentColor" />
  </svg>
);

export const PlusIcon: React.FC<IconProps> = ({ size = 13, className }) => (
  <svg width={size} height={size} viewBox="0 0 16 16" fill="none" className={className} aria-hidden="true">
    <path d="M8 3.2v9.6M3.2 8h9.6" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" />
  </svg>
);

export const UploadBatchIcon: React.FC<IconProps> = ({ size = 13, className }) => (
  <svg width={size} height={size} viewBox="0 0 18 18" fill="none" className={className} aria-hidden="true">
    <path d="M9 11.8V3.5M5.8 6.5L9 3.3l3.2 3.2" stroke="currentColor" strokeWidth="1.65" strokeLinecap="round" strokeLinejoin="round" />
    <path d="M3.2 12.2v1.6a1.4 1.4 0 0 0 1.4 1.4h8.8a1.4 1.4 0 0 0 1.4-1.4v-1.6" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
  </svg>
);

export const ClockRangeIcon: React.FC<IconProps> = ({ size = 13, className }) => (
  <svg width={size} height={size} viewBox="0 0 18 18" fill="none" className={className} aria-hidden="true">
    <circle cx="9" cy="9" r="6.3" stroke="currentColor" strokeWidth="1.6" />
    <path d="M9 5.6V9.2l2.4 1.4" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
  </svg>
);

export const CpuModelIcon: React.FC<IconProps> = ({ size = 13, className }) => (
  <svg width={size} height={size} viewBox="0 0 18 18" fill="none" className={className} aria-hidden="true">
    <rect x="4.8" y="4.8" width="8.4" height="8.4" rx="1.8" stroke="currentColor" strokeWidth="1.55" />
    <path d="M7.5 2.5v2M10.5 2.5v2M7.5 13.5v2M10.5 13.5v2M2.5 7.5h2M2.5 10.5h2M13.5 7.5h2M13.5 10.5h2" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
  </svg>
);

export const WinMinIcon: React.FC = () => (
  <svg width="11" height="11" viewBox="0 0 12 12" fill="none" aria-hidden="true">
    <path d="M2 6h8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
  </svg>
);

export const WinMaxIcon: React.FC<{ maximized?: boolean }> = ({ maximized }) => (
  <svg width="11" height="11" viewBox="0 0 12 12" fill="none" aria-hidden="true">
    {maximized ? (
      <>
        <rect x="2.2" y="3.8" width="6" height="6" rx="1.2" stroke="currentColor" strokeWidth="1.3" />
        <path d="M4.2 2.2h5a1 1 0 0 1 1 1v5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
      </>
    ) : (
      <rect x="2.2" y="2.2" width="7.6" height="7.6" rx="1.4" stroke="currentColor" strokeWidth="1.3" />
    )}
  </svg>
);

export const WinCloseIcon: React.FC = () => (
  <svg width="11" height="11" viewBox="0 0 12 12" fill="none" aria-hidden="true">
    <path d="M2.5 2.5l7 7M9.5 2.5l-7 7" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
  </svg>
);

export const PlugTakeoverIcon: React.FC<IconProps> = ({ size = 12, className }) => (
  <svg width={size} height={size} viewBox="0 0 18 18" fill="none" className={className} aria-hidden="true">
    <path d="M7 2.8v3M11 2.8v3M5.2 5.8h7.6v2.8a3.8 3.8 0 0 1-7.6 0V5.8Z" stroke="currentColor" strokeWidth="1.55" strokeLinecap="round" strokeLinejoin="round" />
    <path d="M9 12.4v2.8" stroke="currentColor" strokeWidth="1.55" strokeLinecap="round" />
  </svg>
);

export const PlayLaunchIcon: React.FC<IconProps> = ({ size = 12, className }) => (
  <svg width={size} height={size} viewBox="0 0 16 16" fill="none" className={className} aria-hidden="true">
    <path d="M5 3.6l7.4 4.4L5 12.4V3.6Z" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round" />
  </svg>
);
