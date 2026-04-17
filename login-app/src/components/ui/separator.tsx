import { cn } from '@/lib/utils';

interface SeparatorProps {
  label?: string;
  className?: string;
}

export function Separator({ label, className }: SeparatorProps) {
  return (
    <div className={cn('flex items-center gap-3 my-4 text-gray-400 dark:text-gray-500 text-[13px]', className)}>
      <div className="flex-1 h-px bg-gray-200 dark:bg-gray-800" />
      {label && <span>{label}</span>}
      <div className="flex-1 h-px bg-gray-200 dark:bg-gray-800" />
    </div>
  );
}
