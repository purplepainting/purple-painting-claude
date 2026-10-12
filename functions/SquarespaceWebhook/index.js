const https = require('https');

function jtPost(payload, context) {
  return new Promise((resolve, reject) => {
    const body = JSON.stringify(payload);
    const options = {
      hostname: 'api.jobtread.com',
      path: '/pave',
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Content-Length': Buffer.byteLength(body)
      }
    };
    const req = https.request(options, (res) => {
      let data = '';
      res.on('data', chunk => data += chunk);
      res.on('end', () => {
        context.log('JT status:', res.statusCode);
        context.log('JT response:', data);
        try { resolve(JSON.parse(data)); } catch { resolve(data); }
      });
    });
    req.on('error', reject);
    req.write(body);
    req.end();
  });
}

function mapLeadSource(raw) {
    if (!raw) return 'Website Form';
    const r = raw.toLowerCase();
    if (r.includes('google') || r.includes('gclid')) return 'Google Ad';
    if (r.includes('meta') || r.includes('facebook') || r.includes('fbclid')) return 'Meta';
    if (r.includes('nextdoor') || r.includes('ndclid')) return 'NextDoor';
    if (r.includes('organic')) return 'Website Form';
    if (r.includes('referral')) return 'Referral';
    if (r.includes('website')) return 'Website Form';
    return 'Website Form';
}

module.exports = async function (context, req) {
  context.log('Squarespace form submission received');
  context.log('RAW BODY:', JSON.stringify(req.body, null, 2));

  const corsHeaders = {
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Methods': 'POST, OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type'
  };

  if (req.method === 'OPTIONS') {
    context.res = { status: 200, headers: corsHeaders, body: '' };
    return;
  }

  const fields = req.body?.data?.formData || req.body?.formData || [];

  function getField(label) {
    const field = fields.find(f =>
      f.label?.toLowerCase() === label.toLowerCase()
    );
    return field?.value?.trim() || '';
  }

  function getCheckboxes(label) {
    return fields
      .filter(f => f.label?.toLowerCase() === label.toLowerCase())
      .map(f => f.value)
      .join(', ');
  }

  const name           = getField('contact.name');
  const phone          = getField('contact.phone');
  const needs          = getCheckboxes('checkbox-b6d1a5b9-ac29-41dc-9ea7-7d8b8f829601-field');
const message = getField('contact.message');
  const rawLeadSource  = getField('SQF_ID_TRACKING');
  const leadSource     = mapLeadSource(rawLeadSource);

  context.log(`Name: ${name}, Phone: ${phone}, Raw: ${rawLeadSource}, Mapped: ${leadSource}, Needs: ${needs}, Message: ${message}`);

  try {
    // Step 1 — Create Account with Lead Source, Needs, and Notes
    const accountResult = await jtPost({
      query: {
        $: { grantKey: process.env.JOBTREAD_API_KEY },
        createAccount: {
          $: {
            organizationId: process.env.JT_ORG_ID,
            name: name || 'Unknown',
            type: 'customer',
customFieldValues: {
    '22PWNYKhTU6S': leadSource || '',        // Lead Source
    '22PWPRpRwScq': needs ? needs.split(', ') : [],  // Needs as array
    '22PWPRuNEwcN': message || ''            // Notes
}
          },
          createdAccount: { id: {}, name: {} }
        }
      }
    }, context);

    const accountId = accountResult?.createAccount?.createdAccount?.id;

    if (!accountId) {
      context.log.error('Failed to create account:', JSON.stringify(accountResult));
      context.res = { status: 500, body: 'Failed to create account', headers: corsHeaders };
      return;
    }

    context.log('Account created:', accountId);

    // Step 2 — Create Contact with phone and Lead Stage
    const contactResult = await jtPost({
      query: {
        $: { grantKey: process.env.JOBTREAD_API_KEY },
        createContact: {
          $: {
            accountId,
            name: name || 'Unknown',
            customFieldValues: {
              '22PWNYKhWqBE': phone || '',              // Phone
              '22PWsFuiWzEq': 'New Lead — Uncontacted'  // Lead Stage
            }
          },
          createdContact: { id: {}, name: {} }
        }
      }
    }, context);

    context.log('Contact created:', JSON.stringify(contactResult));

    context.res = { status: 200, body: 'OK', headers: corsHeaders };

  } catch (err) {
    context.log.error('Error:', err.message);
    context.res = { status: 500, body: 'Server error', headers: corsHeaders };
  }
};