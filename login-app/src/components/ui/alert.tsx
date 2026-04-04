import { type HTMLAttributes, forwardRef } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '@/lib/utils';

const alertVariants = cva(
  'rounded-md border px-3 py-2.5 text-sm mb-4',
  {
    variants: {
      variant: {
        error: 'bg-red-50 text-red-800 border-red-200',
        success: 'bg-green-50 text-green-800 border-green-200',
      },
    },
    defaultVariants: {
      variant: 'error',
    },
  }
);

export interface AlertProps
  extends HTMLAttributes<HTMLDivElement>,
    VariantProps<typeof alertVariants> {}

const Alert = forwardRef<HTMLDivElement, AlertProps>(
  ({ className, variant, ...props }, ref) => (
    <div ref={ref} className={cn(alertVariants({ variant, className }))} {...props} />
  )
);
Alert.displayName = 'Alert';

export { Alert, alertVariants };
