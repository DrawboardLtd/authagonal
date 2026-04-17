import { type LabelHTMLAttributes, forwardRef } from 'react';
import { cn } from '@/lib/utils';

const Label = forwardRef<HTMLLabelElement, LabelHTMLAttributes<HTMLLabelElement>>(
  ({ className, ...props }, ref) => {
    return (
      <label
        ref={ref}
        className={cn('block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1.5', className)}
        {...props}
      />
    );
  }
);
Label.displayName = 'Label';

export { Label };
