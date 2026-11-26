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

export const SystemThemeIcon: React.FC<IconProps> = ({ size = 17, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className} aria-hidden="true">
    <rect x="3" y="4.5" width="18" height="12.5" rx="2.2" stroke="currentColor" strokeWidth="1.75" />
    <path d="M8.5 20h7M12 17v3" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" />
    <path d="M12 7.5a3.2 3.2 0 0 1 0 6.4V7.5Z" fill="currentColor" />
    <circle cx="12" cy="10.7" r="3.2" stroke="currentColor" strokeWidth="1.4" />
  </svg>
);

export const GitHubIcon: React.FC<IconProps> = ({ size = 17, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="currentColor" className={className} aria-hidden="true">
    <path d="M12 .5C5.65.5.5 5.65.5 12c0 5.08 3.29 9.39 7.86 10.91.57.1.78-.25.78-.55 0-.27-.01-1.17-.02-2.12-3.2.7-3.88-1.36-3.88-1.36-.52-1.33-1.28-1.68-1.28-1.68-1.05-.72.08-.7.08-.7 1.16.08 1.77 1.19 1.77 1.19 1.03 1.77 2.7 1.26 3.36.96.1-.75.4-1.26.73-1.55-2.55-.29-5.24-1.28-5.24-5.68 0-1.26.45-2.28 1.19-3.08-.12-.29-.52-1.46.11-3.05 0 0 .97-.31 3.18 1.18a11.1 11.1 0 0 1 5.79 0c2.21-1.49 3.18-1.18 3.18-1.18.63 1.59.23 2.76.11 3.05.74.8 1.19 1.82 1.19 3.08 0 4.41-2.69 5.39-5.25 5.67.41.36.78 1.06.78 2.14 0 1.54-.01 2.79-.01 3.17 0 .31.21.66.79.55A11.51 11.51 0 0 0 23.5 12C23.5 5.65 18.35.5 12 .5Z" />
  </svg>
);

export const SeatBrandIcon: React.FC<{ seatId: string; size?: number; className?: string }> = ({ seatId, size = 22, className }) => {
  const uid = React.useId().replace(/:/g, '');

  if (seatId === 'claude') {
    // Claude 官方品牌星芒标（Anthropic #D97757）
    return (
      <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className} style={{ flexShrink: 0 }} aria-hidden="true">
        <path
          d="M4.709 15.955l4.72-2.647.08-.23-.08-.128H9.2l-.79-.048-2.698-.073-2.339-.097-2.266-.122-.571-.121L0 11.784l.055-.352.48-.321.686.06 1.52.103 2.278.158 1.652.097 2.449.255h.389l.055-.157-.134-.098-.103-.097-2.358-1.596-2.552-1.688-1.336-.972-.724-.491-.364-.462-.158-1.008.656-.722.881.06.225.061.893.686 1.908 1.476 2.491 1.833.365.304.145-.103.019-.073-.164-.274-1.355-2.446-1.446-2.49-.644-1.032-.17-.619a2.97 2.97 0 01-.104-.729L6.283.134 6.696 0l.996.134.42.364.62 1.414 1.002 2.229 1.555 3.03.456.898.243.832.091.255h.158V9.01l.128-1.706.237-2.095.23-2.695.08-.76.376-.91.747-.492.584.28.48.685-.067.444-.286 1.851-.559 2.903-.364 1.942h.212l.243-.242.985-1.306 1.652-2.064.73-.82.85-.904.547-.431h1.033l.76 1.129-.34 1.166-1.064 1.347-.881 1.142-1.264 1.7-.79 1.36.073.11.188-.02 2.856-.606 1.543-.28 1.841-.315.833.388.091.395-.328.807-1.969.486-2.309.462-3.439.813-.042.03.049.061 1.549.146.662.036h1.622l3.02.225.79.522.474.638-.079.485-1.215.62-1.64-.389-3.829-.91-1.312-.329h-.182v.11l1.093 1.068 2.006 1.81 2.509 2.33.127.578-.322.455-.34-.049-2.205-1.657-.851-.747-1.926-1.62h-.128v.17l.444.649 2.345 3.521.122 1.08-.17.353-.608.213-.668-.122-1.374-1.925-1.415-2.167-1.143-1.943-.14.08-.674 7.254-.316.37-.729.28-.607-.461-.322-.747.322-1.476.389-1.924.315-1.53.286-1.9.17-.632-.012-.042-.14.018-1.434 1.967-2.18 2.945-1.726 1.845-.414.164-.717-.37.067-.662.401-.589 2.388-3.036 1.44-1.882.93-1.086-.006-.158h-.055L4.132 18.56l-1.13.146-.487-.456.061-.746.231-.243 1.908-1.312-.006.006z"
          fill="#D97757"
          fillRule="nonzero"
        />
      </svg>
    );
  }

  if (seatId === 'chatgpt') {
    // OpenAI / ChatGPT 官方六瓣花结标（#10A37F）
    return (
      <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className} style={{ flexShrink: 0 }} aria-hidden="true">
        <path
          d="M9.205 8.658v-2.26c0-.19.072-.333.238-.428l4.543-2.616c.619-.357 1.356-.523 2.117-.523 2.854 0 4.662 2.212 4.662 4.566 0 .167 0 .357-.024.547l-4.71-2.759a.797.797 0 00-.856 0l-5.97 3.473zm10.609 8.8V12.06c0-.333-.143-.57-.429-.737l-5.97-3.473 1.95-1.118a.433.433 0 01.476 0l4.543 2.617c1.309.76 2.189 2.378 2.189 3.948 0 1.808-1.07 3.473-2.76 4.163zM7.802 12.703l-1.95-1.142c-.167-.095-.239-.238-.239-.428V5.899c0-2.545 1.95-4.472 4.591-4.472 1 0 1.927.333 2.712.928L8.23 5.067c-.285.166-.428.404-.428.737v6.898zM12 15.128l-2.795-1.57v-3.33L12 8.658l2.795 1.57v3.33L12 15.128zm1.796 7.23c-1 0-1.927-.332-2.712-.927l4.686-2.712c.285-.166.428-.404.428-.737v-6.898l1.974 1.142c.167.095.238.238.238.428v5.233c0 2.545-1.974 4.472-4.614 4.472zm-5.637-5.303l-4.544-2.617c-1.308-.761-2.188-2.378-2.188-3.948A4.482 4.482 0 014.21 6.327v5.423c0 .333.143.571.428.738l5.947 3.449-1.95 1.118a.432.432 0 01-.476 0zm-.262 3.9c-2.688 0-4.662-2.021-4.662-4.519 0-.19.024-.38.047-.57l4.686 2.71c.286.167.571.167.856 0l5.97-3.448v2.26c0 .19-.07.333-.237.428l-4.543 2.616c-.619.357-1.356.523-2.117.523zm5.899 2.83a5.947 5.947 0 005.827-4.756C22.287 18.339 24 15.84 24 13.296c0-1.665-.713-3.282-1.998-4.448.119-.5.19-.999.19-1.498 0-3.401-2.759-5.947-5.946-5.947-.642 0-1.26.095-1.88.31A5.962 5.962 0 0010.205 0a5.947 5.947 0 00-5.827 4.757C1.713 5.447 0 7.945 0 10.49c0 1.666.713 3.283 1.998 4.448-.119.5-.19 1-.19 1.499 0 3.401 2.759 5.946 5.946 5.946.642 0 1.26-.095 1.88-.309a5.96 5.96 0 004.162 1.713z"
          fill="#10A37F"
          fillRule="evenodd"
        />
      </svg>
    );
  }

  if (seatId === 'agy' || seatId === 'antigravity') {
    // Google Antigravity 官方彩色流光标（按实例隔离 mask/filter ID 避免多图标冲突）
    const m0 = `agy-m0-${uid}`;
    const f1 = `agy-f1-${uid}`;
    const f2 = `agy-f2-${uid}`;
    const f3 = `agy-f3-${uid}`;
    const f4 = `agy-f4-${uid}`;
    const f5 = `agy-f5-${uid}`;
    const f6 = `agy-f6-${uid}`;
    const f7 = `agy-f7-${uid}`;
    const f8 = `agy-f8-${uid}`;
    const f9 = `agy-f9-${uid}`;
    const f10 = `agy-f10-${uid}`;
    const f11 = `agy-f11-${uid}`;
    return (
      <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className} style={{ flexShrink: 0 }} aria-hidden="true">
        <mask id={m0} maskUnits="userSpaceOnUse" x="0" y="1" width="24" height="23">
          <path
            d="M21.751 22.607c1.34 1.005 3.35.335 1.508-1.508C17.73 15.74 18.904 1 12.037 1 5.17 1 6.342 15.74.815 21.1c-2.01 2.009.167 2.511 1.507 1.506 5.192-3.517 4.857-9.714 9.715-9.714 4.857 0 4.522 6.197 9.714 9.715z"
            fill="#fff"
          />
        </mask>
        <g mask={`url(#${m0})`}>
          <g filter={`url(#${f1})`}>
            <path d="M-1.018-3.992c-.408 3.591 2.686 6.89 6.91 7.37 4.225.48 7.98-2.043 8.387-5.633.408-3.59-2.686-6.89-6.91-7.37-4.225-.479-7.98 2.043-8.387 5.633z" fill="#FFE432" />
          </g>
          <g filter={`url(#${f2})`}>
            <path d="M15.269 7.747c1.058 4.557 5.691 7.374 10.348 6.293 4.657-1.082 7.575-5.653 6.516-10.21-1.058-4.556-5.691-7.374-10.348-6.292-4.657 1.082-7.575 5.653-6.516 10.21z" fill="#FC413D" />
          </g>
          <g filter={`url(#${f3})`}>
            <path d="M-12.443 10.804c1.338 4.703 7.36 7.11 13.453 5.378 6.092-1.733 9.947-6.95 8.61-11.652C8.282-.173 2.26-2.58-3.833-.848-9.925.884-13.78 6.1-12.443 10.804z" fill="#00B95C" />
          </g>
          <g filter={`url(#${f4})`}>
            <path d="M-12.443 10.804c1.338 4.703 7.36 7.11 13.453 5.378 6.092-1.733 9.947-6.95 8.61-11.652C8.282-.173 2.26-2.58-3.833-.848-9.925.884-13.78 6.1-12.443 10.804z" fill="#00B95C" />
          </g>
          <g filter={`url(#${f5})`}>
            <path d="M-7.608 14.703c3.352 3.424 9.126 3.208 12.896-.483 3.77-3.69 4.108-9.459.756-12.883C2.69-2.087-3.083-1.871-6.853 1.82c-3.77 3.69-4.108 9.458-.755 12.883z" fill="#00B95C" />
          </g>
          <g filter={`url(#${f6})`}>
            <path d="M9.932 27.617c1.04 4.482 5.384 7.303 9.7 6.3 4.316-1.002 6.971-5.448 5.93-9.93-1.04-4.483-5.384-7.304-9.7-6.301-4.316 1.002-6.971 5.448-5.93 9.93z" fill="#3186FF" />
          </g>
          <g filter={`url(#${f7})`}>
            <path d="M2.572-8.185C.392-3.329 2.778 2.472 7.9 4.771c5.122 2.3 11.042.227 13.222-4.63 2.18-4.855-.205-10.656-5.327-12.955-5.122-2.3-11.042-.227-13.222 4.63z" fill="#FBBC04" />
          </g>
          <g filter={`url(#${f8})`}>
            <path d="M-3.267 38.686c-5.277-2.072 3.742-19.117 5.984-24.83 2.243-5.712 8.34-8.664 13.616-6.592 5.278 2.071 11.533 13.482 9.29 19.195-2.242 5.713-23.613 14.298-28.89 12.227z" fill="#3186FF" />
          </g>
          <g filter={`url(#${f9})`}>
            <path d="M28.71 17.471c-1.413 1.649-5.1.808-8.236-1.878-3.135-2.687-4.531-6.201-3.118-7.85 1.412-1.649 5.1-.808 8.235 1.878s4.532 6.2 3.119 7.85z" fill="#749BFF" />
          </g>
          <g filter={`url(#${f10})`}>
            <path d="M18.163 9.077c5.81 3.93 12.502 4.19 14.946.577 2.443-3.612-.287-9.727-6.098-13.658-5.81-3.931-12.502-4.19-14.946-.577-2.443 3.612.287 9.727 6.098 13.658z" fill="#FC413D" />
          </g>
          <g filter={`url(#${f11})`}>
            <path d="M-.915 2.684c-1.44 3.473-.97 6.967 1.05 7.804 2.02.837 4.824-1.3 6.264-4.772 1.44-3.473.97-6.967-1.05-7.804-2.02-.837-4.824 1.3-6.264 4.772z" fill="#FFEE48" />
          </g>
        </g>
        <defs>
          <filter id={f1} colorInterpolationFilters="sRGB" filterUnits="userSpaceOnUse" x="-3.288" y="-11.917" width="19.838" height="17.587">
            <feFlood floodOpacity="0" result="BackgroundImageFix" />
            <feBlend in="SourceGraphic" in2="BackgroundImageFix" result="shape" />
            <feGaussianBlur stdDeviation="1.117" />
          </filter>
          <filter id={f2} colorInterpolationFilters="sRGB" filterUnits="userSpaceOnUse" x="4.251" y="-13.493" width="38.9" height="38.565">
            <feFlood floodOpacity="0" result="BackgroundImageFix" />
            <feBlend in="SourceGraphic" in2="BackgroundImageFix" result="shape" />
            <feGaussianBlur stdDeviation="5.4" />
          </filter>
          <filter id={f3} colorInterpolationFilters="sRGB" filterUnits="userSpaceOnUse" x="-21.889" y="-10.592" width="40.955" height="36.517">
            <feFlood floodOpacity="0" result="BackgroundImageFix" />
            <feBlend in="SourceGraphic" in2="BackgroundImageFix" result="shape" />
            <feGaussianBlur stdDeviation="4.591" />
          </filter>
          <filter id={f4} colorInterpolationFilters="sRGB" filterUnits="userSpaceOnUse" x="-21.889" y="-10.592" width="40.955" height="36.517">
            <feFlood floodOpacity="0" result="BackgroundImageFix" />
            <feBlend in="SourceGraphic" in2="BackgroundImageFix" result="shape" />
            <feGaussianBlur stdDeviation="4.591" />
          </filter>
          <filter id={f5} colorInterpolationFilters="sRGB" filterUnits="userSpaceOnUse" x="-19.099" y="-10.278" width="36.632" height="36.595">
            <feFlood floodOpacity="0" result="BackgroundImageFix" />
            <feBlend in="SourceGraphic" in2="BackgroundImageFix" result="shape" />
            <feGaussianBlur stdDeviation="4.591" />
          </filter>
          <filter id={f6} colorInterpolationFilters="sRGB" filterUnits="userSpaceOnUse" x=".981" y="8.758" width="33.533" height="34.087">
            <feFlood floodOpacity="0" result="BackgroundImageFix" />
            <feBlend in="SourceGraphic" in2="BackgroundImageFix" result="shape" />
            <feGaussianBlur stdDeviation="4.363" />
          </filter>
          <filter id={f7} colorInterpolationFilters="sRGB" filterUnits="userSpaceOnUse" x="-6.143" y="-21.659" width="35.978" height="35.276">
            <feFlood floodOpacity="0" result="BackgroundImageFix" />
            <feBlend in="SourceGraphic" in2="BackgroundImageFix" result="shape" />
            <feGaussianBlur stdDeviation="3.954" />
          </filter>
          <filter id={f8} colorInterpolationFilters="sRGB" filterUnits="userSpaceOnUse" x="-11.96" y="-.46" width="45.114" height="46.523">
            <feFlood floodOpacity="0" result="BackgroundImageFix" />
            <feBlend in="SourceGraphic" in2="BackgroundImageFix" result="shape" />
            <feGaussianBlur stdDeviation="3.531" />
          </filter>
          <filter id={f9} colorInterpolationFilters="sRGB" filterUnits="userSpaceOnUse" x="10.485" y=".58" width="25.094" height="24.054">
            <feFlood floodOpacity="0" result="BackgroundImageFix" />
            <feBlend in="SourceGraphic" in2="BackgroundImageFix" result="shape" />
            <feGaussianBlur stdDeviation="3.159" />
          </filter>
          <filter id={f10} colorInterpolationFilters="sRGB" filterUnits="userSpaceOnUse" x="5.833" y="-12.467" width="33.508" height="30.007">
            <feFlood floodOpacity="0" result="BackgroundImageFix" />
            <feBlend in="SourceGraphic" in2="BackgroundImageFix" result="shape" />
            <feGaussianBlur stdDeviation="2.669" />
          </filter>
          <filter id={f11} colorInterpolationFilters="sRGB" filterUnits="userSpaceOnUse" x="-8.355" y="-8.876" width="22.194" height="26.151">
            <feFlood floodOpacity="0" result="BackgroundImageFix" />
            <feBlend in="SourceGraphic" in2="BackgroundImageFix" result="shape" />
            <feGaussianBlur stdDeviation="3.303" />
          </filter>
        </defs>
      </svg>
    );
  }

  // Cursor 官方 3D 等距立方体光标标（深浅主题自适应单色）
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="currentColor" fillRule="evenodd" className={className} style={{ flexShrink: 0 }} aria-hidden="true">
      <path d="M22.106 5.68L12.5.135a.998.998 0 00-.998 0L1.893 5.68a.84.84 0 00-.419.726v11.186c0 .3.16.577.42.727l9.607 5.547a.999.999 0 00.998 0l9.608-5.547a.84.84 0 00.42-.727V6.407a.84.84 0 00-.42-.726zm-.603 1.176L12.228 22.92c-.063.108-.228.064-.228-.061V12.34a.59.59 0 00-.295-.51l-9.11-5.26c-.107-.062-.063-.228.062-.228h18.55c.264 0 .428.286.296.514z" />
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
