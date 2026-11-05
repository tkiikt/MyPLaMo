Add-Type -AssemblyName System.Windows.Forms
$form = New-Object Windows.Forms.Form
$form.Text = 'MyPLaMo selection test'
$edit = New-Object Windows.Forms.TextBox
$edit.Multiline = $true
$edit.Dock = 'Fill'
$edit.Text = 'The sky is blue.'
$form.Controls.Add($edit)
$form.Add_Shown({$edit.Focus();$edit.SelectAll()})
[void]$form.ShowDialog()

