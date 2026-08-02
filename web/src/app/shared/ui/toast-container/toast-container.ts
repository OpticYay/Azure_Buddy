import { Component, inject } from '@angular/core';

import { ToastService } from '../../../core/services/toast.service';
import { toastSlide } from '../../animations';

@Component({
  selector: 'app-toast-container',
  imports: [],
  templateUrl: './toast-container.html',
  styleUrl: './toast-container.css',
  animations: [toastSlide],
})
export class ToastContainer {
  protected readonly toastService = inject(ToastService);
}
